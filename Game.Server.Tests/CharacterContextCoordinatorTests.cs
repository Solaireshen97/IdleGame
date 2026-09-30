using System.Net;
using System.Net.Http.Json;
using Game.Client.Services;
using Game.Shared.Dtos.Auth;
using Game.Shared.Dtos.Characters;
using Microsoft.JSInterop;
using Xunit;

namespace Game.Server.Tests;

public sealed class CharacterContextCoordinatorTests
{
    [Fact]
    public async Task HeaderRosterAndForcedRefreshShareAnInFlightSnapshot()
    {
        var pending = Completion<HttpResponseMessage>();
        var entered = Completion<bool>();
        var characterReads = 0;
        var userReads = 0;
        var rosterReads = 0;
        using var http = Client(request =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/user/character":
                    characterReads++;
                    return Task.FromResult(Ok(Character(1)));
                case "/api/user/characters":
                    rosterReads++;
                    return Task.FromResult(Ok(new List<CharacterSummaryResponse> { new() { CharacterId = 1, IsCurrent = true } }));
                default:
                    userReads++;
                    entered.TrySetResult(true);
                    return pending.Task;
            }
        });
        using var api = new ApiService(http, new UserSessionService(new Storage()));
        using var characters = new ActiveCharacterState(api);
        using var balance = new AccountBalanceState(characters, api);
        using var context = new CharacterContextCoordinator(api, characters, balance);
        var header = context.EnsureLoadedAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var roster = context.LoadRosterAsync();
        var forced = context.RefreshAsync();
        pending.SetResult(Ok(User(1, 10)));
        await Task.WhenAll(header, roster, forced);

        Assert.Equal(1, characterReads);
        Assert.Equal(1, userReads);
        Assert.Equal(1, rosterReads);
        Assert.Equal(10, balance.Gold);
        Assert.Same(context.CurrentUser, (await roster)!.User);
        Assert.Equal(1, (await roster)!.Characters!.Single().CharacterId);
    }

    [Fact]
    public async Task CharacterSelectionClearsAllOwnedSnapshotsAndConsumersShareRecovery()
    {
        var activeId = 1;
        var characterReads = 0;
        var userReads = 0;
        using var http = Client(request =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/user/character/select":
                    activeId = 2;
                    return Task.FromResult(Ok(new CharacterSummaryResponse { CharacterId = activeId }));
                case "/api/user/character":
                    characterReads++;
                    return Task.FromResult(Ok(Character(activeId)));
                case "/api/user/characters":
                    return Task.FromResult(Ok(new List<CharacterSummaryResponse> { new() { CharacterId = activeId, IsCurrent = true } }));
                default:
                    userReads++;
                    return Task.FromResult(Ok(User(activeId, activeId * 10)));
            }
        });
        using var api = new ApiService(http, new UserSessionService(new Storage()));
        using var characters = new ActiveCharacterState(api);
        using var balance = new AccountBalanceState(characters, api);
        using var context = new CharacterContextCoordinator(api, characters, balance);
        await context.EnsureLoadedAsync();
        await api.SelectCurrentCharacterAsync(2);
        Assert.Null(context.CurrentUser);
        Assert.Null(characters.Current);
        Assert.Null(balance.Gold);
        await Task.WhenAll(context.EnsureLoadedAsync(), context.LoadRosterAsync());

        Assert.Equal(2, characters.Current!.CharacterId);
        Assert.Equal(2, context.CurrentUser!.ActiveCharacterId);
        Assert.Equal(20, balance.Gold);
        Assert.Equal(2, characterReads);
        Assert.Equal(2, userReads);
        balance.Update(1, 999);
        Assert.Equal(20, context.CurrentUser.Gold);
    }

    [Fact]
    public async Task OldAccountHeaderCannotOverwriteNewAccountDuringLoad()
    {
        var pending = Completion<HttpResponseMessage>();
        var entered = Completion<bool>();
        var session = new UserSessionService(new Storage());
        using var http = Client(request =>
        {
            var id = request.Headers.Authorization?.Parameter == "old-account" ? 1 : 2;
            if (request.RequestUri!.AbsolutePath == "/api/user/character")
                return Task.FromResult(Ok(Character(id)));
            if (id == 1) { entered.TrySetResult(true); return pending.Task; }
            return Task.FromResult(Ok(User(id, 20)));
        });
        using var api = new ApiService(http, session);
        using var characters = new ActiveCharacterState(api);
        using var balance = new AccountBalanceState(characters, api);
        using var context = new CharacterContextCoordinator(api, characters, balance);
        var old = context.EnsureLoadedAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.SetToken("new-account");
        Assert.Null(context.CurrentUser);
        await context.EnsureLoadedAsync();
        pending.SetResult(Ok(User(1, 999)));
        Assert.Null(await old);

        Assert.Equal(2, context.CurrentUser!.UserId);
        Assert.Equal(2, characters.Current!.CharacterId);
        Assert.Equal(20, balance.Gold);
        Assert.Equal(20, context.CurrentUser.Gold);
    }

    [Fact]
    public async Task FailedPeriodicReadPreservesKnownContextButLogoutClearsItImmediately()
    {
        var failed = false;
        var session = new UserSessionService(new Storage());
        using var http = Client(request => Task.FromResult(failed
            ? new(HttpStatusCode.ServiceUnavailable)
            : request.RequestUri!.AbsolutePath == "/api/user/character" ? Ok(Character(1)) : Ok(User(1, 10))));
        using var api = new ApiService(http, session);
        using var characters = new ActiveCharacterState(api);
        using var balance = new AccountBalanceState(characters, api);
        using var context = new CharacterContextCoordinator(api, characters, balance);
        await context.EnsureLoadedAsync();
        var known = context.CurrentUser;
        failed = true;
        Assert.Same(known, await context.RefreshAsync());
        Assert.Equal(1, characters.Current!.CharacterId);
        Assert.Equal(10, balance.Gold);

        await session.ClearToken();
        Assert.Null(context.CurrentUser);
        Assert.Null(characters.Current);
        Assert.Null(balance.Gold);
    }

    [Fact]
    public async Task BalanceProjectionOnlyUsesMatchingSelectedCharacter()
    {
        using var http = Client(request => Task.FromResult(request.RequestUri!.AbsolutePath == "/api/user/character"
            ? Ok(Character(1)) : Ok(User(1, 10))));
        using var api = new ApiService(http, new UserSessionService(new Storage()));
        using var characters = new ActiveCharacterState(api);
        using var balance = new AccountBalanceState(characters, api);
        using var context = new CharacterContextCoordinator(api, characters, balance);
        await context.EnsureLoadedAsync();
        balance.Update(1, 25);
        Assert.Equal(25, context.CurrentUser!.Gold);
        balance.Update(2, 999);
        Assert.Equal(25, context.CurrentUser.Gold);
        // User and current-character endpoints disagree persistently: preserve
        // the account summary, but never attach that other character's wallet.
        context.CurrentUser.ActiveCharacterId = 2;
        balance.Update(1, 30);
        Assert.Equal(25, context.CurrentUser.Gold);
    }

    private static CurrentCharacterResponse Character(int id) => new() { CharacterId = id, Name = $"Character {id}" };
    private static CurrentUserResponse User(int id, int gold) => new()
    {
        UserId = id, UserName = $"User {id}", ActiveCharacterId = id, CharacterCount = 1,
        CharacterSlotLimit = 2, MaximumCharacterSlots = 5, Gold = gold
    };
    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) =>
        new(new Handler(send)) { BaseAddress = new("http://localhost/") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
    private sealed class Storage : IJSRuntime
    {
        private string? _token = "old-account";
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
