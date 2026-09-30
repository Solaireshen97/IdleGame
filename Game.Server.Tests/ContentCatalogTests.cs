using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Game.Client.Services;
using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Dtos.Characters;
using Microsoft.JSInterop;
using Xunit;

namespace Game.Server.Tests;

public sealed class ContentCatalogTests
{
    [Fact]
    public void ContentVersionIncludesSchemaAndNestedDefinitions()
    {
        var payload = Catalog();
        var first = ContentCatalogStore.CreateDocument(payload);
        Assert.Equal(first, ContentCatalogStore.CreateDocument(payload));
        payload.Dungeons[0].Monsters[0].Attack++;
        var changedMonster = ContentCatalogStore.CreateDocument(payload);
        Assert.NotEqual(first.Version, changedMonster.Version);
        payload.SchemaVersion++;
        Assert.NotEqual(changedMonster.Version, ContentCatalogStore.CreateDocument(payload).Version);
    }

    [Fact]
    public void PublicDungeonDefinitionExcludesEveryPersonalProgressField()
    {
        var summary = new DungeonSummaryResponse
        {
            DungeonId = 10, Code = "test", CurrentCharacterLevel = 40,
            UnlockedDepth = 9, CharacterHighestDepth = 8, MasteryLevel = 4,
            GoldBonusPercent = 10, KillExtraRollChancePercent = 20, ClearExtraRollChancePercent = 30,
            CanEnter = true, LockReason = "personal", IsClearedByCurrentUser = true, AutoUnlocked = true,
            Depths = [new() { DepthLevel = 1, IsUnlocked = true }]
        };
        using var json = JsonDocument.Parse(ContentCatalogStore.CreateDocument(new()
        {
            Dungeons = [DungeonDefinitionResponse.FromSummary(summary)]
        }).Json);
        var definition = json.RootElement.GetProperty("dungeons")[0];
        foreach (var property in new[] { "currentCharacterLevel", "unlockedDepth", "characterHighestDepth", "masteryLevel",
            "goldBonusPercent", "killExtraRollChancePercent", "clearExtraRollChancePercent", "canEnter", "lockReason",
            "isClearedByCurrentUser", "autoUnlocked" })
            Assert.False(definition.TryGetProperty(property, out _), property);
        Assert.False(definition.GetProperty("depths")[0].TryGetProperty("isUnlocked", out _));
    }

    [Fact]
    public async Task ConcurrentCatalogReadersShareRequestButReceiveIndependentNestedCopies()
    {
        var pending = Completion<HttpResponseMessage>();
        var calls = 0;
        using var client = Client((request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            calls++;
            return pending.Task;
        });
        var cache = new ContentCatalogCache(client);
        var readers = Enumerable.Range(0, 12).Select(_ => cache.GetAsync()).ToArray();
        Assert.Equal(1, calls);
        pending.SetResult(Content(Catalog()));
        var results = await Task.WhenAll(readers);
        results[0]!.Dungeons[0].Monsters[0].Attack = 999;
        results[0]!.Dungeons[0].Depths[0].AddedMechanics.Add("page-only");
        results[0]!.Professions[0].Name = "page-only";

        Assert.All(results.Skip(1), result =>
        {
            Assert.Equal(10, result!.Dungeons[0].Monsters[0].Attack);
            Assert.Single(result.Dungeons[0].Depths[0].AddedMechanics);
            Assert.Equal("骑士", result.Professions[0].Name);
        });
        Assert.Equal(10, (await cache.GetAsync())!.Dungeons[0].Monsters[0].Attack);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CatalogRevalidatesAfterSixtySecondsAndHandles304ThenNewVersion()
    {
        var clock = new ManualClock();
        var first = Catalog("first");
        var next = Catalog("second");
        next.Professions[0].Name = "新职业名称";
        var calls = 0;
        using var client = Client((request, _) =>
        {
            calls++;
            if (calls == 1) return Task.FromResult(Content(first));
            Assert.Equal("\"first\"", Assert.Single(request.Headers.IfNoneMatch).Tag);
            return Task.FromResult(calls == 2 ? new HttpResponseMessage(HttpStatusCode.NotModified) : Content(next));
        });
        var cache = new ContentCatalogCache(client, clock);
        Assert.Equal("first", (await cache.GetAsync())!.Version);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal("first", (await cache.GetAsync())!.Version);
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("first", (await cache.GetAsync())!.Version);
        Assert.Equal(2, calls);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal("新职业名称", (await cache.GetAsync())!.Professions[0].Name);
        Assert.Equal("second", (await cache.GetAsync())!.Version);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task AccountSwitchReusesDefinitionsButNeverPreviousProgress()
    {
        var storage = new TokenStorage();
        var session = new UserSessionService(storage);
        var catalogReads = 0;
        var progressReads = 0;
        using var client = Client((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/content")
            {
                catalogReads++;
                Assert.Null(request.Headers.Authorization);
                return Task.FromResult(Content(Catalog()));
            }
            var id = request.Headers.Authorization?.Parameter == "first-account" ? 1 : 2;
            if (request.RequestUri.AbsolutePath == "/api/user/character")
                return Task.FromResult(Ok(new CurrentCharacterResponse { CharacterId = id }));
            Assert.Equal("/api/content/progress", request.RequestUri.AbsolutePath);
            progressReads++;
            return Task.FromResult(Ok(Progress(id)));
        });
        using var api = new ApiService(client, session);
        var first = Assert.Single((await api.GetMergedDungeonsAsync())!);
        Assert.True(first.AutoUnlocked);
        Assert.Equal(3, first.UnlockedDepth);
        Assert.Null(first.LockReason);
        await session.SetToken("second-account");
        var second = Assert.Single((await api.GetMergedDungeonsAsync())!);
        Assert.False(second.AutoUnlocked);
        Assert.Equal(1, second.UnlockedDepth);
        Assert.Equal(1, catalogReads);
        Assert.Equal(2, progressReads);
        second.Monsters[0].Attack = 999;
        Assert.Equal(10, Assert.Single((await api.GetMergedDungeonsAsync())!).Monsters[0].Attack);
        Assert.Equal(2, progressReads);
    }

    [Fact]
    public async Task LatePreviousAccountProgressIsRejectedAfterNewAccountCompletes()
    {
        var session = new UserSessionService(new TokenStorage());
        var oldProgress = Completion<HttpResponseMessage>();
        var entered = Completion<bool>();
        using var client = Client((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/content") return Task.FromResult(Content(Catalog()));
            var id = request.Headers.Authorization?.Parameter == "first-account" ? 1 : 2;
            if (request.RequestUri.AbsolutePath == "/api/user/character")
                return Task.FromResult(Ok(new CurrentCharacterResponse { CharacterId = id }));
            if (id == 2) return Task.FromResult(Ok(Progress(2)));
            entered.SetResult(true);
            return oldProgress.Task;
        });
        using var api = new ApiService(client, session);
        var first = api.GetMergedDungeonsAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.SetToken("second-account");
        var second = Assert.Single((await api.GetMergedDungeonsAsync())!);
        oldProgress.SetResult(Ok(Progress(1)));
        Assert.Null(await first);
        Assert.False(second.AutoUnlocked);
        Assert.Equal(1, Assert.Single((await api.GetMergedDungeonsAsync())!).UnlockedDepth);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OppositeCharacterAndProgressCacheAgesRecoverBothSnapshotsOnce(bool staleProgress)
    {
        var activeId = 1;
        var characterReads = 0;
        var progressReads = 0;
        var catalogReads = 0;
        using var client = Client((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/content")
            {
                catalogReads++;
                return Task.FromResult(Content(Catalog()));
            }
            if (request.RequestUri.AbsolutePath == "/api/user/character")
            {
                characterReads++;
                return Task.FromResult(Ok(new CurrentCharacterResponse { CharacterId = activeId }));
            }
            progressReads++;
            return Task.FromResult(Ok(Progress(activeId)));
        });
        using var api = new ApiService(client, new UserSessionService(new TokenStorage()));
        if (staleProgress) Assert.NotNull(await api.GetMergedDungeonsAsync());
        else Assert.Equal(1, (await api.GetCurrentCharacterAsync())!.CharacterId);
        activeId = 2; // The same token switches character in another window.
        if (staleProgress) Assert.Equal(2, (await api.GetCurrentCharacterAsync(forceRefresh: true))!.CharacterId);

        var recovered = Assert.Single((await api.GetMergedDungeonsAsync())!);
        Assert.False(recovered.AutoUnlocked);
        Assert.Equal(1, recovered.UnlockedDepth);
        Assert.Equal(2, progressReads);
        Assert.Equal(staleProgress ? 3 : 2, characterReads);
        Assert.Equal(1, catalogReads);
        Assert.Equal(1, Assert.Single((await api.GetMergedDungeonsAsync())!).UnlockedDepth);
        Assert.Equal(2, progressReads);
    }

    [Fact]
    public async Task ContinuouslyChangingCharacterProgressStopsAfterOneRecovery()
    {
        var characterReads = 0;
        var progressReads = 0;
        using var client = Client((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/content") return Task.FromResult(Content(Catalog()));
            if (request.RequestUri.AbsolutePath == "/api/user/character")
            {
                characterReads++;
                return Task.FromResult(Ok(new CurrentCharacterResponse { CharacterId = 1 }));
            }
            progressReads++;
            return Task.FromResult(Ok(Progress(2)));
        });
        using var api = new ApiService(client, new UserSessionService(new TokenStorage()));
        Assert.Null(await api.GetMergedDungeonsAsync());
        Assert.Equal(2, characterReads);
        Assert.Equal(2, progressReads);
    }

    private static ContentCatalogResponse Catalog(string version = "initial") => new()
    {
        Version = version,
        Professions = [new() { Code = "knight", Name = "骑士" }],
        Dungeons = [new()
        {
            DungeonId = 10, Code = "test", SupportsDepths = true, MaximumDepth = 3,
            Monsters = [new() { Name = "Test monster", Attack = 10 }],
            Depths = [new() { DepthLevel = 1, AddedMechanics = ["fixed"] }, new() { DepthLevel = 2 }, new() { DepthLevel = 3 }]
        }]
    };

    private static DungeonProgressResponse Progress(int characterId) => new()
    {
        UserId = characterId, CharacterId = characterId, CurrentCharacterLevel = characterId * 10,
        Dungeons = [new()
        {
            DungeonId = 10, UnlockedDepth = characterId == 1 ? 3 : 1,
            AutoUnlocked = characterId == 1, IsClearedByCurrentUser = characterId == 1, CanEnter = true
        }]
    };

    private static HttpResponseMessage Content(ContentCatalogResponse value)
    {
        var response = Ok(value);
        response.Headers.ETag = new EntityTagHeaderValue($"\"{value.Version}\"");
        return response;
    }
    private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new Handler(send)) { BaseAddress = new Uri("http://localhost/") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    private sealed class TokenStorage : IJSRuntime
    {
        private string? _token = "first-account";
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "localStorage.getItem") return ValueTask.FromResult((TValue)(object?)_token!);
            if (identifier == "localStorage.setItem") _token = (string)args![1]!;
            if (identifier == "localStorage.removeItem") _token = null;
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
