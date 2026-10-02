using System.Net;
using Game.Client.Services;
using Game.Shared.Dtos;
using Xunit;

namespace Game.Server.Tests;

public sealed class BattleStatisticsClientTests
{
    private static BattleStatisticsRequestKey Key() => new(7, 1, "current", 3, 10, 20);
    private static BattleStatisticsResponse Response(BattleStatisticsRequestKey key, int version = 8) => new()
    {
        RoomId = key.RoomId, Scope = key.Scope, RunSequence = key.RunSequence,
        MonsterId = key.MonsterId, CharacterId = key.CharacterId, RoomVersion = version
    };

    [Theory]
    [InlineData("account")]
    [InlineData("room")]
    [InlineData("scope")]
    [InlineData("run")]
    [InlineData("monster")]
    [InlineData("character")]
    [InlineData("same")]
    public void StatisticsNewRequestCancelsPreviousAndRejectsLateResponse(string change)
    {
        using var coordinator = new BattleStatisticsLoadCoordinator();
        var oldKey = Key(); var old = coordinator.Begin(oldKey);
        var nextKey = change switch
        {
            "account" => oldKey with { SessionRevision = 2 }, "room" => oldKey with { RoomId = 8 },
            "scope" => oldKey with { Scope = "previous", RunSequence = 2 },
            "run" => oldKey with { RunSequence = 4 }, "monster" => oldKey with { MonsterId = 11 },
            "character" => oldKey with { CharacterId = 21 }, _ => oldKey
        };
        var next = coordinator.Begin(nextKey);
        Assert.True(old.Token.IsCancellationRequested); Assert.False(next.Token.IsCancellationRequested);
        Assert.False(coordinator.Accept(oldKey, old.Generation, Response(oldKey)));
        Assert.True(coordinator.Accept(nextKey, next.Generation, Response(nextKey)));
    }

    [Theory]
    [InlineData("room")]
    [InlineData("scope")]
    [InlineData("run")]
    [InlineData("monster")]
    [InlineData("character")]
    public void StatisticsResponseMustMatchEveryRequestedIdentity(string mismatch)
    {
        using var coordinator = new BattleStatisticsLoadCoordinator();
        var key = Key() with { Scope = "run" }; var request = coordinator.Begin(key); var value = Response(key);
        switch (mismatch)
        {
            case "room": value.RoomId++; break;
            case "scope": value.Scope = "previous"; break;
            case "run": value.RunSequence++; break;
            case "monster": value.MonsterId++; break;
            case "character": value.CharacterId++; break;
        }
        Assert.False(coordinator.Accept(key, request.Generation, value));
        Assert.True(coordinator.Accept(key, request.Generation, Response(key)));
    }

    [Fact]
    public void StatisticsFollowingCurrentAcceptsAdvancedRunButRejectsEarlierRun()
    {
        using var coordinator = new BattleStatisticsLoadCoordinator();
        var key = Key() with { MonsterId = null, CharacterId = null };
        var request = coordinator.Begin(key);
        var earlier = Response(key, 9); earlier.RunSequence = 2;
        Assert.False(coordinator.Accept(key, request.Generation, earlier));
        var advanced = Response(key, 10); advanced.RunSequence = 4;
        Assert.True(coordinator.Accept(key, request.Generation, advanced));
    }

    [Fact]
    public void StatisticsVersionFloorSurvivesRefreshAndScopeSwitchButDoesNotRejectNewerThanUi()
    {
        using var coordinator = new BattleStatisticsLoadCoordinator();
        var key = Key(); var first = coordinator.Begin(key);
        // The last UI snapshot can be older than the statistics endpoint's committed version.
        Assert.True(coordinator.Accept(key, first.Generation, Response(key, 20)));
        var other = key with { CharacterId = 21 }; var otherRequest = coordinator.Begin(other);
        Assert.True(coordinator.Accept(other, otherRequest.Generation, Response(other, 12)));
        var refresh = coordinator.Begin(key);
        Assert.False(coordinator.Accept(key, refresh.Generation, Response(key, 19)));
        Assert.True(coordinator.Accept(key, refresh.Generation, Response(key, 20)));
        Assert.True(coordinator.Accept(key, refresh.Generation, Response(key, 21)));
        Assert.False(coordinator.Accept(key, refresh.Generation, Response(key, 20)));
    }

    [Fact]
    public void StatisticsRoomScopeHasNoSingleRunAndCancelRejectsPendingResult()
    {
        using var coordinator = new BattleStatisticsLoadCoordinator();
        var key = Key() with { Scope = "room", RunSequence = null, MonsterId = null };
        var request = coordinator.Begin(key);
        var response = Response(key); response.CurrentRunSequence = 9;
        Assert.True(coordinator.Accept(key, request.Generation, response));
        coordinator.Cancel();
        Assert.True(request.Token.IsCancellationRequested); Assert.Null(coordinator.Current);
        Assert.False(coordinator.Accept(key, request.Generation, response));
    }
}

public sealed partial class ApiRequestScopeTests
{
    [Fact]
    public async Task StatisticsApiCarriesEscapedScopeAndOptionalFilters()
    {
        var uris = new List<Uri>();
        using var client = Client(request => { uris.Add(request.RequestUri!); return Task.FromResult(Ok(new BattleStatisticsResponse { RoomId = 7 })); });
        using var api = new ApiService(client, new UserSessionService(new Storage()));
        Assert.NotNull(await api.GetBattleStatisticsAsync(7, "previous &detail", 3, 10, 20));
        Assert.Equal("/api/rooms/7/statistics", uris[0].AbsolutePath);
        Assert.Equal("?scope=previous%20%26detail&runSequence=3&monsterId=10&characterId=20", uris[0].Query);
        Assert.NotNull(await api.GetBattleStatisticsAsync(7, "room"));
        Assert.Equal("?scope=room", uris[1].Query);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatisticsApiRejectsOldAccountResponseWithoutClearingNewAccount(bool unauthorized)
    {
        var entered = Completion<bool>(); var pending = Completion<HttpResponseMessage>();
        var storage = new Storage(); var session = new UserSessionService(storage);
        using var client = Client(_ => { entered.TrySetResult(true); return pending.Task; });
        using var api = new ApiService(client, session);
        var request = api.GetBattleStatisticsAsync(7, "current", 3);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.SetToken("new-account");
        pending.SetResult(unauthorized ? new(HttpStatusCode.Unauthorized) : Ok(new BattleStatisticsResponse { RoomId = 7 }));
        Assert.Null(await request); Assert.Equal("new-account", await session.GetToken());
        Assert.Equal(0, storage.Removals);
    }

    [Fact]
    public async Task StatisticsApiCurrentUnauthorizedExpiresSessionAndWrongRoomIsRejected()
    {
        var storage = new Storage();
        using var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var api = new ApiService(client, new UserSessionService(storage));
        Assert.Null(await api.GetBattleStatisticsAsync(7, "current"));
        Assert.Null(storage.Token); Assert.Equal(1, storage.Removals);
        using var wrongClient = Client(_ => Task.FromResult(Ok(new BattleStatisticsResponse { RoomId = 8 })));
        using var wrongApi = new ApiService(wrongClient, new UserSessionService(new Storage()));
        Assert.Null(await wrongApi.GetBattleStatisticsAsync(7, "current"));
    }

    [Fact]
    public async Task StatisticsApiCallerCancellationReachesPendingTransport()
    {
        var entered = Completion<bool>(); var cancelled = Completion<bool>();
        using var client = new HttpClient(new StatisticsCancellationHandler(entered, cancelled)) { BaseAddress = new("http://localhost/") };
        using var api = new ApiService(client, new UserSessionService(new Storage()));
        using var cancellation = new CancellationTokenSource();
        var request = api.GetBattleStatisticsAsync(7, "current", cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
        Assert.True(await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class StatisticsCancellationHandler(TaskCompletionSource<bool> entered, TaskCompletionSource<bool> cancelled) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            entered.TrySetResult(true);
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { cancelled.TrySetResult(true); throw; }
            throw new InvalidOperationException("The transport must end through cancellation.");
        }
    }
}
