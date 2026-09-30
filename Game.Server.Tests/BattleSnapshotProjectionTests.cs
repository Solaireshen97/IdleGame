using Game.Client.Services;
using Game.Shared.Dtos;
using System.Net;
using System.Net.Http.Json;
using Microsoft.JSInterop;
using Xunit;

namespace Game.Server.Tests;

public sealed class BattleSnapshotProjectionTests
{
    [Theory]
    [InlineData("known", "known", RoomLoadStatus.Success)]
    [InlineData("known", "unknown", RoomLoadStatus.RetryableError)]
    [InlineData("", "known", RoomLoadStatus.RetryableError)]
    public async Task SnapshotUsesOneRequestAndAcceptsOnlyTheRequestedProjection(
        string requested, string returned, RoomLoadStatus expected)
    {
        var handler = new SnapshotHandler(returned);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var api = new ApiService(http, new UserSessionService(new SessionStorage()));
        var result = await api.LoadBattleSnapshotAsync(new() { RoomId = 2, ProjectionId = requested });
        Assert.Equal(expected, result.Status);
        Assert.Equal(1, handler.Requests);
        if (expected == RoomLoadStatus.Success)
        {
            Assert.Null(result.Room);
            Assert.Equal(2, result.Synchronization!.Unchanged!.RoomId);
        }
    }

    [Fact]
    public void UnchangedResponsePreservesActionsAndRewardsButReplacesHistoryWithoutMutatingPreviousView()
    {
        var previous = new RoomDetailResponse
        {
            RoomId = 2, RoomVersion = 5, MonsterHp = 90, CanPrepare = true,
            Slots = [new() { CharacterId = 9, CharacterHp = 80 }],
            CumulativeRewards = new(), BattleLogs = [new() { Id = 1, Text = "old" }]
        };
        var response = new BattleSyncResponse
        {
            ProjectionId = "known", Unchanged = new()
            {
                RoomId = 2, RoomVersion = 5, ServerTimeUtc = DateTime.UtcNow, BattleHistoryEpoch = "epoch",
                BattleLogs = [new() { Id = 2, Text = "late" }]
            }
        };
        var updated = BattleSnapshotProjection.Resolve(previous, "known", response);
        Assert.NotSame(previous, updated);
        Assert.Equal(90, updated!.MonsterHp);
        Assert.True(updated.CanPrepare);
        Assert.Equal(9, Assert.Single(updated.Slots).CharacterId);
        Assert.Same(previous.CumulativeRewards, updated.CumulativeRewards);
        Assert.Equal("late", Assert.Single(updated.BattleLogs).Text);
        Assert.Equal("old", Assert.Single(previous.BattleLogs).Text);
        Assert.Equal(response.Unchanged.ServerTimeUtc, updated.ServerTimeUtc);
    }

    [Theory]
    [InlineData("", 2, 5)]
    [InlineData("other", 2, 5)]
    [InlineData("known", 3, 5)]
    [InlineData("known", 2, 4)]
    public void UnchangedResponseRequiresTheExactBaseline(string id, int roomId, int version)
    {
        var result = BattleSnapshotProjection.Resolve(new() { RoomId = 2, RoomVersion = 5 }, id,
            new() { ProjectionId = "known", Unchanged = new() { RoomId = roomId, RoomVersion = version } });
        Assert.Null(result);
    }

    private sealed class SnapshotHandler(string projectionId) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/battle/snapshot", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = JsonContent.Create(new BattleSyncResponse
                    { ProjectionId = projectionId, Unchanged = new() { RoomId = 2, RoomVersion = 5 } })
            });
        }
    }

    private sealed class SessionStorage : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult((TValue)(object)"test-session");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}
