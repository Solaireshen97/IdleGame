using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Game.Client.Services;
using Game.Shared.Dtos.Characters;
using Game.Shared.Dtos.Inventory;
using Microsoft.JSInterop;
using Xunit;

namespace Game.Server.Tests;

public sealed class InventoryClientStateTests
{
    [Fact]
    public async Task ForceRefreshStartsNewReadAndDisplacedCompletionCannotRepopulateCache()
    {
        var first = Completion<HttpResponseMessage>();
        var entered = Completion<bool>();
        var reads = 0;
        using var http = Client(_ =>
        {
            if (++reads == 1) { entered.SetResult(true); return first.Task; }
            return Task.FromResult(Ok(Overview(2, 90)));
        });
        using var api = Api(http);
        var stale = api.GetInventoryAsync(2, new());
        await entered.Task;
        var fresh = await api.GetInventoryAsync(2, new(), forceRefresh: true);
        first.SetResult(Ok(Overview(2, 10)));
        Assert.Null((await stale).Response);
        Assert.Equal(90, fresh.Response!.Gold);
        Assert.Equal(90, (await api.GetInventoryAsync(2, new())).Response!.Gold);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task PreviewNeitherInvalidatesInventoryNorNotifiesSubscribers()
    {
        var reads = 0;
        using var http = Client(request =>
        {
            if (request.Method == HttpMethod.Get) { reads++; return Task.FromResult(Ok(Overview(2, 7))); }
            return Task.FromResult(Ok(Preview(2)));
        });
        using var api = Api(http);
        var notifications = new List<int>();
        api.InventoryChanged += notifications.Add;
        using var state = new InventoryState(api);
        await state.LoadAsync(2, new());
        var preview = await api.PreviewInventoryActionAsync(2, new() { AssetKind = InventoryKinds.Weapon, Action = "sell", InstanceIds = [11] });
        Assert.True(preview.Response!.Allowed);
        Assert.Empty(notifications);
        Assert.False(state.IsStale);
        Assert.Equal(7, (await api.GetInventoryAsync(2, new())).Response!.Gold);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task ExplicitCharacterWriteSurvivesGlobalSelectionAndNotifiesItsOwner()
    {
        var entered = Completion<bool>();
        var pending = Completion<HttpResponseMessage>();
        using var http = Client(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/user/character/select")
                return Task.FromResult(Ok(new CharacterSummaryResponse { CharacterId = 1 }));
            Assert.Equal("/api/user/characters/2/weapons/sell", request.RequestUri.AbsolutePath);
            entered.SetResult(true);
            return pending.Task;
        });
        using var api = Api(http);
        var notifications = new List<int>();
        api.InventoryChanged += notifications.Add;
        var execution = api.ExecuteInventoryActionAsync(2, Preview(2));
        await entered.Task;
        await api.SelectCurrentCharacterAsync(1);
        pending.SetResult(Ok(new CharacterWeaponsResponse { CharacterId = 2, Gold = 20 }));
        Assert.True((await execution).Success);
        Assert.Equal([2], notifications);
    }

    [Fact]
    public async Task LegacyRetryAfterLostResponseReusesPreviewAndRequestIdentity()
    {
        var previews = 0;
        var requests = new List<WeaponBatchRequest>();
        using var http = Client(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/preview"))
            { previews++; return Ok(Preview(2)); }
            requests.Add((await request.Content!.ReadFromJsonAsync<WeaponBatchRequest>())!);
            if (requests.Count == 1) throw new HttpRequestException("Response lost after commit.");
            return Ok(new CharacterWeaponsResponse { CharacterId = 2, Gold = 20 });
        });
        using var api = Api(http);
        Assert.Null((await api.SellWeaponsAsync(2, 11)).Response);
        Assert.NotNull((await api.SellWeaponsAsync(2, 11)).Response);
        Assert.Equal(1, previews);
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0].RequestId, requests[1].RequestId);
        Assert.False(string.IsNullOrEmpty(requests[1].RequestId));
        Assert.Equal("same-outcome", requests[1].OutcomeFingerprint);
        Assert.Equal(4, Assert.Single(requests[1].ExpectedVersions).Version);
        Assert.Equal([11], requests[1].WeaponIds);
    }

    [Fact]
    public async Task SamePreviewCanRetryAnUncertainExecutionWithoutGeneratingNewRequestId()
    {
        var ids = new List<string>();
        using var http = Client(async request =>
        {
            ids.Add((await request.Content!.ReadFromJsonAsync<WeaponBatchRequest>())!.RequestId!);
            return ids.Count == 1 ? new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("temporarily unavailable") }
                : Ok(new CharacterWeaponsResponse { CharacterId = 2 });
        });
        using var api = Api(http);
        var preview = Preview(2);
        Assert.False((await api.ExecuteInventoryActionAsync(2, preview)).Success);
        Assert.True((await api.ExecuteInventoryActionAsync(2, preview)).Success);
        Assert.Equal(ids[0], ids[1]);
    }

    [Fact]
    public async Task LateDifferentCharacterReadCannotReplaceCurrentViewAndFailedRefreshKeepsStock()
    {
        var old = Completion<HttpResponseMessage>();
        var entered = Completion<bool>();
        var fail = false;
        using var http = Client(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/1/")) { entered.SetResult(true); return old.Task; }
            return Task.FromResult(fail ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("offline") }
                : Ok(Overview(2, 90)));
        });
        using var api = Api(http);
        using var state = new InventoryState(api);
        var first = state.LoadAsync(1, new());
        await entered.Task;
        await state.LoadAsync(2, new());
        old.SetResult(Ok(Overview(1, 10)));
        await first;
        Assert.Equal(2, state.Overview!.CharacterId);
        Assert.Equal(90, state.Overview.Gold);
        fail = true;
        await state.LoadAsync(2, new(), forceRefresh: true);
        Assert.Equal(90, state.Overview.Gold);
        Assert.NotNull(state.ErrorMessage);
        Assert.True(state.IsStale);
        Assert.False(state.IsLoading);
    }

    [Fact]
    public async Task ForceRefreshInSameViewRejectsOlderDataAndSelectionChangeKeepsLocalOwner()
    {
        var pending = Completion<HttpResponseMessage>();
        var entered = Completion<bool>();
        var reads = 0;
        using var http = Client(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/select"))
                return Task.FromResult(Ok(new CharacterSummaryResponse { CharacterId = 1 }));
            if (++reads == 1) { entered.SetResult(true); return pending.Task; }
            return Task.FromResult(Ok(Overview(2, 90)));
        });
        using var api = Api(http);
        using var state = new InventoryState(api);
        var old = state.LoadAsync(2, new());
        await entered.Task;
        await state.LoadAsync(2, new(), forceRefresh: true);
        pending.SetResult(Ok(Overview(2, 10)));
        await old;
        Assert.Equal(90, state.Overview!.Gold);
        await api.SelectCurrentCharacterAsync(1);
        Assert.Equal(2, state.Overview.CharacterId);
        Assert.Equal(90, state.Overview.Gold);
    }

    [Fact]
    public async Task LoadedCategoryBelongsToCompletedSnapshotEvenWhenNextCategoryFails()
    {
        var pending = Completion<HttpResponseMessage>();
        var entered = Completion<bool>();
        var reads = 0;
        using var http = Client(_ =>
        {
            if (++reads == 1) { entered.SetResult(true); return pending.Task; }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        using var api = Api(http);
        using var state = new InventoryState(api);
        var query = new InventoryQueryRequest { Category = InventoryCategories.Weapons };
        var first = state.LoadAsync(2, query);
        await entered.Task;
        query.Category = InventoryCategories.Supplies;
        pending.SetResult(Ok(Overview(2, 90)));
        await first;
        Assert.Equal(InventoryCategories.Weapons, state.LoadedCategory);
        await state.LoadAsync(2, query);
        Assert.Equal(InventoryCategories.Weapons, state.LoadedCategory);
        Assert.Equal(90, state.Overview!.Gold);
        Assert.NotNull(state.ErrorMessage);
        await state.LoadAsync(3, query);
        Assert.Null(state.LoadedCategory);
        Assert.Null(state.Overview);
    }

    [Fact]
    public async Task SessionChangeImmediatelyClearsPreviousAccountsInventory()
    {
        using var http = Client(_ => Task.FromResult(Ok(Overview(2, 90))));
        var session = new UserSessionService(new Storage());
        using var api = new ApiService(http, session);
        using var state = new InventoryState(api);
        await state.LoadAsync(2, new());
        await session.SetToken("different-account");
        Assert.Null(state.Overview);
        Assert.Null(state.LoadedCategory);
        Assert.True(state.IsStale);
    }

    [Fact]
    public async Task MutationInvalidatesPendingReadAndNotifiesOnlyAffectedCharacter()
    {
        var pending = Completion<HttpResponseMessage>();
        var entered = Completion<bool>();
        var reads = 0;
        using var http = Client(request =>
        {
            if (request.Method == HttpMethod.Put) return Task.FromResult(Ok(new CharacterWeaponsResponse { CharacterId = 2 }));
            if (++reads == 1) { entered.SetResult(true); return pending.Task; }
            return Task.FromResult(Ok(Overview(2, 30)));
        });
        using var api = Api(http);
        using var state = new InventoryState(api);
        var old = state.LoadAsync(2, new());
        await entered.Task;
        await api.SetWeaponLockAsync(2, 11, true);
        Assert.True(state.IsStale);
        await state.LoadAsync(2, new(), forceRefresh: true);
        pending.SetResult(Ok(Overview(2, 10)));
        await old;
        Assert.Equal(30, state.Overview!.Gold);
        Assert.False(state.IsStale);
    }

    [Fact]
    public async Task FullQueryAndRawDetailCodeAreEncodedAndUsedInCacheKey()
    {
        var urls = new List<string>();
        using var http = Client(request =>
        {
            urls.Add(request.RequestUri!.OriginalString);
            return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("/details")
                ? Ok(new InventoryItemDetailDto { CharacterId = 2, Entry = new() { Code = "item/a?b&c" } })
                : Ok(Overview(2, 7)));
        });
        using var api = Api(http);
        await api.GetInventoryAsync(2, new() { Search = "a&b", QualityRank = 1, Page = 2 });
        await api.GetInventoryAsync(2, new() { Search = "a&b", QualityRank = 2, Page = 2 });
        await api.GetInventoryDetailAsync(2, InventoryKinds.Stack, "item/a?b&c");
        Assert.Equal(3, urls.Count);
        Assert.Contains("search=a%26b", urls[0]);
        Assert.Contains("qualityRank=1", urls[0]);
        Assert.Contains("qualityRank=2", urls[1]);
        Assert.Contains("key=item%2Fa%3Fb%26c", urls[2]);
    }

    [Fact]
    public async Task ResponseForAnotherCharacterIsRejectedAndCannotPoisonRequestedOwnersCache()
    {
        var reads = 0;
        using var http = Client(_ => Task.FromResult(Ok(Overview(++reads == 1 ? 1 : 2, 90))));
        using var api = Api(http);
        Assert.Null((await api.GetInventoryAsync(2, new())).Response);
        Assert.Equal(2, (await api.GetInventoryAsync(2, new())).Response!.CharacterId);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task SoulDismantleUsesOriginalDomainRouteAndConfirmedVersions()
    {
        SoulImprintBatchRequest? submitted = null;
        using var http = Client(async request =>
        {
            Assert.Equal("/api/user/characters/2/soul-imprints/dismantle", request.RequestUri!.AbsolutePath);
            submitted = await request.Content!.ReadFromJsonAsync<SoulImprintBatchRequest>();
            return Ok(new CharacterSoulImprintsResponse { CharacterId = 2 });
        });
        using var api = Api(http);
        var preview = Preview(2);
        preview.AssetKind = InventoryKinds.SoulImprint;
        preview.Action = "dismantle";
        Assert.True((await api.ExecuteInventoryActionAsync(2, preview)).Success);
        Assert.Equal([11], submitted!.SoulImprintIds);
        Assert.Equal(4, Assert.Single(submitted.ExpectedVersions).Version);
        Assert.Equal("same-outcome", submitted.OutcomeFingerprint);
    }

    [Theory]
    [InlineData("upgrade")]
    [InlineData("craft")]
    public async Task UncertainGrowthCommandKeepsIdentityAndSeparatesChangedInputsAndSessions(string action)
    {
        var ids = new List<string>();
        using var http = Client(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            ids.Add(body.RootElement.GetProperty("requestId").GetString()!);
            return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("DatabaseBusy") };
        });
        var session = new UserSessionService(new Storage());
        using var api = new ApiService(http, session);
        async Task Send(int variant)
        {
            var result = action == "upgrade" ? await api.UpgradeWeaponQualityAsync(2, 11, 20 + variant)
                : await api.CraftWeaponBreakthroughStoneAsync(2, 1, 2 + variant);
            Assert.Null(result.Response);
        }
        await Send(0);
        await Send(0);
        await Send(1);
        await Send(0);
        await session.SetToken("different-account");
        await Send(0);
        Assert.All(ids, id => Assert.True(Guid.TryParseExact(id, "N", out _)));
        Assert.Equal(ids[0], ids[1]);
        Assert.NotEqual(ids[0], ids[2]);
        Assert.Equal(ids[0], ids[3]);
        Assert.NotEqual(ids[0], ids[4]);
    }

    [Theory]
    [InlineData("weapons")]
    [InlineData("consumables")]
    [InlineData("souls")]
    public async Task ExplicitLoadoutRefreshDisplacesInFlightReadAndKeepsAuthoritativeCache(string resource)
    {
        var pending = Completion<HttpResponseMessage>();
        var entered = Completion<bool>();
        var reads = 0;
        using var http = Client(_ =>
        {
            if (++reads == 1) { entered.SetResult(true); return pending.Task; }
            return Task.FromResult(Ok(new { CharacterId = 2, CharacterName = "snapshot-2" }));
        });
        using var api = Api(http);
        async Task<string?> Read(bool force) => resource switch
        {
            "weapons" => (await api.GetCharacterWeaponsAsync(2, force)).Response?.CharacterName,
            "consumables" => (await api.GetCharacterConsumablesAsync(2, force)).Response?.CharacterName,
            _ => (await api.GetCharacterSoulImprintsAsync(2, force)).Response?.CharacterName
        };
        var displaced = Read(false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("snapshot-2", await Read(true));
        pending.SetResult(Ok(new { CharacterId = 2, CharacterName = "snapshot-1" }));
        Assert.Equal("snapshot-2", await displaced);
        Assert.Equal("snapshot-2", await Read(false));
        Assert.Equal(2, reads);
        Assert.Equal("snapshot-2", await Read(true));
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task MissingWeaponSkillDefinitionProducesActionableEnhancementError()
    {
        using var http = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        { Content = new StringContent("\"UnknownWeaponSkill\"") }));
        using var api = Api(http);
        var result = await api.EnhanceWeaponSkillAsync(2, 11, 1);
        Assert.Null(result.Response);
        Assert.Equal("该武器技能定义暂不可用，无法强化。", result.ErrorMessage);
    }

    private static InventoryOverviewResponse Overview(int id, int gold) => new() { CharacterId = id, Gold = gold };
    private static InventoryActionPreviewResponse Preview(int id) => new()
    {
        CharacterId = id, AssetKind = InventoryKinds.Weapon, Action = "sell", Allowed = true,
        Items = [new() { InstanceId = 11, AssetKind = InventoryKinds.Weapon, Version = 4 }],
        ExpectedVersions = [new() { Id = 11, Version = 4 }], OutcomeFingerprint = "same-outcome"
    };
    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => new(new Handler(send)) { BaseAddress = new("http://localhost/") };
    private static ApiService Api(HttpClient http) => new(http, new UserSessionService(new Storage()));
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
    private sealed class Storage : IJSRuntime
    {
        private string? _token = "test-account";
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "localStorage.getItem") return ValueTask.FromResult((T)(object?)_token!);
            if (identifier == "localStorage.setItem") _token = (string)args![1]!;
            if (identifier == "localStorage.removeItem") _token = null;
            return ValueTask.FromResult(default(T)!);
        }
    }
}
