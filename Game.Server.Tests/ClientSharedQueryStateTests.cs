using System.Net;
using System.Net.Http.Json;
using Game.Client.Services;
using Game.Shared.Dtos.Characters;
using Microsoft.JSInterop;
using Xunit;

namespace Game.Server.Tests;

public sealed class ClientSharedQueryStateTests
{
    [Fact]
    public async Task ConcurrentSummaryConsumersShareRequestAndCompletedCache()
    {
        var pending = Completion<HttpResponseMessage>();
        var entered = Completion<bool>();
        var calls = 0;
        using var client = CreateClient((_, _) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult(true);
            return pending.Task;
        });
        using var api = new ApiService(client, new UserSessionService(new TokenStorage()));
        using var state = new ActiveCharacterState(api);
        var first = state.EnsureLoadedAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var other = state.EnsureLoadedAsync();
        var forced = api.GetCurrentCharacterAsync(forceRefresh: true);
        pending.SetResult(Ok(Character(1)));
        await Task.WhenAll(first, other, forced);

        Assert.Equal(1, calls);
        Assert.Equal(1, state.Current!.CharacterId);
        var anotherConsumer = await api.GetCurrentCharacterAsync();
        Assert.NotSame(state.Current, anotherConsumer);
        Assert.Equal(state.Current.Name, anotherConsumer!.Name);
        state.Current.Name = "local page edit";
        Assert.Equal("Character 1", anotherConsumer.Name);
        var thirdConsumer = await api.GetCurrentCharacterAsync();
        Assert.NotSame(anotherConsumer, thirdConsumer);
        Assert.Equal("Character 1", thirdConsumer!.Name);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SameSessionInvalidationRetriesPendingReadAndReturnsFreshData()
    {
        var pending = Completion<HttpResponseMessage>();
        var entered = Completion<bool>();
        var calls = 0;
        using var client = CreateClient((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult(true);
                return pending.Task;
            }
            return Task.FromResult(Ok(Character(1, attack: 50)));
        });
        using var api = new ApiService(client, new UserSessionService(new TokenStorage()));
        var request = api.GetCurrentCharacterAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        api.InvalidateCharacter();
        pending.SetResult(Ok(Character(1, attack: 5)));

        Assert.Equal(50, (await request)!.Attack);
        Assert.Equal(2, calls);
        Assert.Equal(50, (await api.GetCurrentCharacterAsync())!.Attack);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateUnauthorizedResponseCannotClearANewerLogin(bool externallyChanged)
    {
        var storage = new TokenStorage();
        var session = new UserSessionService(storage);
        var pending = Completion<HttpResponseMessage>();
        var entered = Completion<bool>();
        using var client = CreateClient((request, _) =>
        {
            Assert.Equal("old-token", request.Headers.Authorization?.Parameter);
            entered.SetResult(true);
            return pending.Task;
        });
        using var api = new ApiService(client, session);
        var oldRequest = api.GetCurrentCharacterAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (externallyChanged) storage.Token = "new-token";
        else await session.SetToken("new-token");
        pending.SetResult(new(HttpStatusCode.Unauthorized));

        Assert.Null(await oldRequest);
        Assert.Equal("new-token", storage.Token);
        Assert.Equal("new-token", await session.GetToken());
        Assert.Equal(0, storage.Removals);
    }

    [Fact]
    public async Task LatePreviousSessionSummaryCannotOverwriteNewSharedState()
    {
        var session = new UserSessionService(new TokenStorage());
        var pending = Completion<HttpResponseMessage>();
        var entered = Completion<bool>();
        using var client = CreateClient((request, _) =>
        {
            if (request.Headers.Authorization?.Parameter == "old-token")
            {
                entered.SetResult(true);
                return pending.Task;
            }
            Assert.Equal("new-token", request.Headers.Authorization?.Parameter);
            return Task.FromResult(Ok(Character(2)));
        });
        using var api = new ApiService(client, session);
        using var state = new ActiveCharacterState(api);
        using var balance = new AccountBalanceState(state, api);
        var oldRefresh = state.EnsureLoadedAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.SetToken("new-token");
        await state.EnsureLoadedAsync();
        balance.Update(2, 200);
        pending.SetResult(Ok(Character(1)));
        await oldRefresh;
        balance.Update(1, 999);

        Assert.Equal(2, state.Current!.CharacterId);
        Assert.Equal(2, balance.CharacterId);
        Assert.Equal(200, balance.Gold);
    }

    [Fact]
    public async Task CharacterSelectionClearsSharedCharacterAndBalanceAndRefreshesCache()
    {
        var activeId = 1;
        var characterReads = 0;
        using var client = CreateClient((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/api/user/character/select", request.RequestUri!.AbsolutePath);
                activeId = 2;
                return Task.FromResult(Ok(new CharacterSummaryResponse { CharacterId = 2 }));
            }
            Assert.Equal("/api/user/character", request.RequestUri!.AbsolutePath);
            characterReads++;
            return Task.FromResult(Ok(Character(activeId)));
        });
        using var api = new ApiService(client, new UserSessionService(new TokenStorage()));
        using var state = new ActiveCharacterState(api);
        using var balance = new AccountBalanceState(state, api);
        await state.EnsureLoadedAsync();
        balance.Update(1, 100);
        var (selected, error) = await api.SelectCurrentCharacterAsync(2);

        Assert.Null(error);
        Assert.Equal(2, selected!.CharacterId);
        Assert.Null(state.Current);
        Assert.Null(balance.Gold);
        await state.EnsureLoadedAsync();
        Assert.Equal(2, state.Current!.CharacterId);
        Assert.Equal(2, characterReads);
        balance.Update(1, 999);
        Assert.Null(balance.Gold);
        balance.Update(2, 20);
        Assert.Equal(20, balance.Gold);
    }

    [Fact]
    public async Task AuthoritativeWeaponSummarySurvivesAnOlderPendingRefresh()
    {
        var pending = Completion<HttpResponseMessage>();
        var entered = Completion<bool>();
        var calls = 0;
        using var client = CreateClient((_, _) =>
        {
            if (++calls == 1) return Task.FromResult(Ok(Character(1, attack: 5)));
            entered.TrySetResult(true);
            return pending.Task;
        });
        using var api = new ApiService(client, new UserSessionService(new TokenStorage()));
        using var state = new ActiveCharacterState(api);
        await state.EnsureLoadedAsync();
        var staleRefresh = state.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        state.ApplyWeaponSummary(new CharacterWeaponsResponse
        {
            CharacterId = 1, Hp = 80, EffectiveMaxHp = 120, EffectiveAttack = 50
        });
        pending.SetResult(Ok(Character(1, attack: 5)));
        await staleRefresh;

        Assert.Equal(50, state.Current!.Attack);
        Assert.Equal(80, state.Current.Hp);
        Assert.Equal(120, state.Current.MaxHp);
        Assert.Equal(50, (await api.GetCurrentCharacterAsync())!.Attack);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CurrentUnauthorizedResponseClearsSharedStateAndOnlyCurrentToken()
    {
        var unauthorized = false;
        var storage = new TokenStorage();
        using var client = CreateClient((_, _) => Task.FromResult(unauthorized
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Ok(Character(1))));
        using var api = new ApiService(client, new UserSessionService(storage));
        using var state = new ActiveCharacterState(api);
        using var balance = new AccountBalanceState(state, api);
        await state.EnsureLoadedAsync();
        balance.Update(1, 100);
        unauthorized = true;
        await state.RefreshAsync();

        Assert.Null(storage.Token);
        Assert.Null(state.Current);
        Assert.Null(balance.Gold);
        Assert.Equal(1, storage.Removals);
    }

    private static CurrentCharacterResponse Character(int id, int attack = 20) => new()
    {
        CharacterId = id, Name = $"Character {id}", ProfessionCode = "knight", ProfessionName = "骑士",
        Hp = 100, MaxHp = 100, Attack = attack, Level = 1
    };

    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static HttpClient CreateClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new ResponseHandler(send)) { BaseAddress = new Uri("http://localhost/") };

    private sealed class ResponseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class TokenStorage : IJSRuntime
    {
        public string? Token { get; set; } = "old-token";
        public int Removals { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "localStorage.getItem") return ValueTask.FromResult((TValue)(object?)Token!);
            if (identifier == "localStorage.setItem") Token = (string)args![1]!;
            if (identifier == "localStorage.removeItem") { Token = null; Removals++; }
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
