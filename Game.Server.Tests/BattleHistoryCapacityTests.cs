using Game.Server.Services;
using Game.Shared.Dtos;
using Xunit;

namespace Game.Server.Tests;

public class BattleHistoryCapacityTests
{
    [Fact]
    public void GlobalCapacityEvictsLeastRecentlyPublishedRoomEvenAtSameTimestamp()
    {
        var clock = new Clock();
        var store = new BattleLogStore(clock, maximumRooms: 2);
        store.Append(1, ["first"], DateTime.UtcNow);
        store.Append(2, ["second"], DateTime.UtcNow);
        store.Append(1, ["newer first"], DateTime.UtcNow);
        store.Append(3, ["third"], DateTime.UtcNow);
        Assert.Empty(store.Get(2));
        Assert.Equal(2, store.Get(1).Count);
        Assert.Single(store.Get(3));
    }

    [Fact]
    public void PollingDoesNotExtendPublicationExpiryAndBothHistoriesExpireTogether()
    {
        var clock = new Clock();
        var store = new BattleLogStore(clock, timeToLive: TimeSpan.FromMinutes(10));
        store.Append(1, ["log"], DateTime.UtcNow, [new BattleEventResponse { Target = new("Monster", 8) }]);
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Single(store.GetSnapshot(1).Events);
        clock.Advance(TimeSpan.FromMinutes(1));
        var expired = store.GetSnapshot(1);
        Assert.Empty(expired.Logs); Assert.Empty(expired.Events);
        Assert.Equal(store.Epoch, expired.Epoch);
    }

    [Fact]
    public void CapacityAndExpiryNeverTruncateTheLatestLongSettlement()
    {
        var clock = new Clock();
        var store = new BattleLogStore(clock, maximumRooms: 1);
        store.Append(1, ["old"], DateTime.UtcNow);
        var facts = Enumerable.Range(1, 140).Select(i => new BattleEventResponse { Sequence = i, Target = new("Monster", 8) });
        store.Append(2, Enumerable.Range(1, 120).Select(i => $"log{i}"), DateTime.UtcNow, facts);
        Assert.Empty(store.Get(1));
        var latest = store.GetSnapshot(2);
        Assert.Equal(120, latest.Logs.Count); Assert.Equal(140, latest.Events.Count);
        latest.Events.Clear();
        Assert.Equal(140, store.GetEvents(2).Count);
    }

    [Fact]
    public void NewProcessEpochDistinguishesIdenticalRestartedLogIds()
    {
        var first = new BattleLogStore(); var second = new BattleLogStore();
        first.Append(1, ["old"], DateTime.UtcNow);
        second.Append(1, ["new"], DateTime.UtcNow);
        Assert.Equal(first.Get(1)[0].Id, second.Get(1)[0].Id);
        Assert.NotEqual(first.GetSnapshot(1).Epoch, second.GetSnapshot(1).Epoch);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-09-30T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
