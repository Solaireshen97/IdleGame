using System.Net;
using System.Net.Http.Json;
using Game.Client.Services;
using Game.Shared.Dtos.Auth;
using Game.Shared.Dtos.Characters;
using Microsoft.JSInterop;
using Xunit;

namespace Game.Server.Tests;

public sealed class CharacterUserSnapshotReaderTests
{
    [Fact]
    public async Task CrossWindowCharacterSwitchNeverLabelsNewGoldWithPreviousCharacter()
    {
        var activeId = 1;
        var characterReads = 0;
        var userReads = 0;
        using var client = Client(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/user/character")
            {
                characterReads++;
                return Ok(new CurrentCharacterResponse { CharacterId = activeId });
            }
            userReads++;
            return Ok(new CurrentUserResponse { UserId = 1, ActiveCharacterId = activeId, Gold = activeId * 10 });
        });
        using var api = new ApiService(client, new UserSessionService(new TokenStorage()));
        using var characters = new ActiveCharacterState(api);
        using var balance = new AccountBalanceState(characters, api);
        await characters.EnsureLoadedAsync();
        balance.Update(1, 10);
        var seenBalances = new List<(int? Character, int? Gold)>();
        balance.Changed += () => seenBalances.Add((balance.CharacterId, balance.Gold));
        activeId = 2;

        var recovered = await CharacterUserSnapshotReader.LoadAsync(api, characters, balance);

        Assert.Equal(2, recovered!.ActiveCharacterId);
        Assert.Equal(2, characters.Current!.CharacterId);
        Assert.Equal(2, balance.CharacterId);
        Assert.Equal(20, balance.Gold);
        Assert.DoesNotContain(seenBalances, entry => entry.Character == 1 && entry.Gold == 20);
        Assert.Equal(2, characterReads);
        Assert.Equal(2, userReads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public async Task PersistentlyMismatchedGoldDoesNotUpdateBalanceOrLoop(int? responseCharacterId)
    {
        var characterReads = 0;
        var userReads = 0;
        using var client = Client(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/user/character")
            {
                characterReads++;
                return Ok(new CurrentCharacterResponse { CharacterId = 1 });
            }
            userReads++;
            return Ok(new CurrentUserResponse { UserId = 1, ActiveCharacterId = responseCharacterId, Gold = 999 });
        });
        using var api = new ApiService(client, new UserSessionService(new TokenStorage()));
        using var characters = new ActiveCharacterState(api);
        using var balance = new AccountBalanceState(characters, api);
        await characters.EnsureLoadedAsync();
        balance.Update(1, 10);

        Assert.NotNull(await CharacterUserSnapshotReader.LoadAsync(api, characters, balance));

        Assert.Equal(1, balance.CharacterId);
        Assert.Equal(10, balance.Gold);
        Assert.Equal(2, characterReads);
        Assert.Equal(2, userReads);
    }

    [Fact]
    public async Task MatchingIdentityUpdatesBalanceWithoutCharacterRequery()
    {
        var characterReads = 0;
        using var client = Client(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/user/character")
            {
                characterReads++;
                return Ok(new CurrentCharacterResponse { CharacterId = 1 });
            }
            return Ok(new CurrentUserResponse { UserId = 1, ActiveCharacterId = 1, Gold = 15 });
        });
        using var api = new ApiService(client, new UserSessionService(new TokenStorage()));
        using var characters = new ActiveCharacterState(api);
        using var balance = new AccountBalanceState(characters, api);
        await characters.EnsureLoadedAsync();
        await CharacterUserSnapshotReader.LoadAsync(api, characters, balance);
        Assert.Equal(15, balance.Gold);
        Assert.Equal(1, characterReads);
    }

    [Fact]
    public async Task FrequentContextChangesStopAfterThreeReadsWithoutApplyingGold()
    {
        var storage = new TokenStorage();
        var userReads = 0;
        using var client = Client(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/user/character")
                return Ok(new CurrentCharacterResponse { CharacterId = 1 });
            storage.Token = $"changed-token-{++userReads}";
            return Ok(new CurrentUserResponse { UserId = 1, ActiveCharacterId = 1, Gold = 999 });
        });
        using var api = new ApiService(client, new UserSessionService(storage));
        using var characters = new ActiveCharacterState(api);
        using var balance = new AccountBalanceState(characters, api);
        await characters.EnsureLoadedAsync();
        balance.Update(1, 10);

        Assert.Null(await CharacterUserSnapshotReader.LoadAsync(api, characters, balance));
        Assert.Equal(3, userReads);
        Assert.Null(balance.Gold);
    }

    private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> send) =>
        new(new Handler(send)) { BaseAddress = new Uri("http://localhost/") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
    private sealed class TokenStorage : IJSRuntime
    {
        public string Token { get; set; } = "same-token";
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(identifier == "localStorage.getItem" ? (TValue)(object)Token : default!);
    }
}
