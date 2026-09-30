using Game.Client.Services;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Xunit;

namespace Game.Server.Tests;

public class BattleFeedbackCoordinatorTests
{
    [Fact]
    public void LateCommittedFactsUseOriginalSceneAndPlayOnlyOnce()
    {
        var coordinator = new BattleFeedbackCoordinator();
        var before = Room(0, 4);
        var empty = Room(1, 5);
        Assert.Null(coordinator.Observe(before, empty, true));
        var complete = Room(1, 5, facts: true);
        var playback = coordinator.Observe(empty, complete, true)!.Value;
        Assert.Same(before, playback.Scene);
        Assert.Equal(20, playback.Plan.TotalDamage);
        Assert.Null(coordinator.Observe(complete, complete, true));
        // An older scene cannot cause the same settlement to play again.
        Assert.Null(coordinator.Observe(before, complete, true));
    }

    [Fact]
    public void RestartedEventIdsDoNotHideNewSettlement()
    {
        var before = Room(0, 4);
        before.BattleEvents.Add(new() { Id = 1000, Target = new("Monster", 8) });
        var coordinator = new BattleFeedbackCoordinator();
        Assert.NotNull(coordinator.Observe(before, Room(1, 5, facts: true), true));
    }

    [Fact]
    public void OlderResponseCannotConsumePendingScene()
    {
        var coordinator = new BattleFeedbackCoordinator();
        var before = Room(0, 4);
        var empty = Room(1, 5);
        coordinator.Observe(before, empty, true);
        Assert.True(BattleFeedbackCoordinator.IsStale(empty, before));
        Assert.Null(coordinator.Observe(empty, before, true));
        Assert.NotNull(coordinator.Observe(empty, Room(1, 5, facts: true), true));
    }

    [Fact]
    public void ClosingKillingRoundCanReceiveLateFacts()
    {
        var coordinator = new BattleFeedbackCoordinator();
        var before = Room(0, 4);
        var closed = Room(1, 5); closed.ClosedAtUtc = DateTime.UtcNow; closed.MonsterHp = 0;
        coordinator.Observe(before, closed, true);
        Assert.True(coordinator.BeginClosedRetry());
        var completed = Room(1, 5, facts: true); completed.ClosedAtUtc = closed.ClosedAtUtc; completed.MonsterHp = 0;
        completed.BattleEvents.Add(completed.BattleEvents[0] with
        { Id = 2, Sequence = 2, Kind = BattleEventKind.Defeat, ActualAmount = 0 });
        Assert.True(coordinator.Observe(closed, completed, true)!.Value.Plan.Defeated);
        Assert.False(coordinator.NeedsClosedRetry);
    }

    [Fact]
    public void ClosedRetriesAreBoundedEvenWhenAllRequestsFail()
    {
        var coordinator = new BattleFeedbackCoordinator();
        var closed = Room(1, 5); closed.ClosedAtUtc = DateTime.UtcNow;
        coordinator.Observe(Room(0, 4), closed, true);
        for (var i = 0; i < 3; i++) Assert.True(coordinator.BeginClosedRetry());
        Assert.False(coordinator.BeginClosedRetry());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NavigationOrSkippedRoundDropsOldPendingScene(bool newRun)
    {
        var coordinator = new BattleFeedbackCoordinator();
        var empty = Room(1, 5);
        coordinator.Observe(Room(0, 4), empty, true);
        var next = Room(newRun ? 1 : 3, 6, facts: true);
        if (newRun) next.RunSequence = 2;
        Assert.Null(coordinator.Observe(empty, next, true));
        Assert.False(coordinator.NeedsClosedRetry);
    }

    [Fact]
    public void SuppressedPresentationDoesNotReplayLater()
    {
        var coordinator = new BattleFeedbackCoordinator();
        var empty = Room(1, 5);
        coordinator.Observe(Room(0, 4), empty, true);
        coordinator.Observe(empty, empty, false);
        Assert.Null(coordinator.Observe(empty, Room(1, 5, facts: true), true));
    }

    [Fact]
    public void LogCursorAcceptsRestartedIdsAndKeepsConstantMemory()
    {
        var cursor = new BattleLogCursor();
        Assert.True(cursor.Accept("first-process", 100));
        Assert.False(cursor.Accept("first-process", 100));
        Assert.False(cursor.Accept("first-process", 90));
        Assert.True(cursor.Accept("second-process", 1));
        Assert.False(cursor.Accept("second-process", 1));
        Assert.True(cursor.Accept("second-process", 2));
        cursor.Reset();
        Assert.True(cursor.Accept("second-process", 1));
        Assert.True(cursor.Accept("second-process", 100, roomId: 1));
        Assert.True(cursor.Accept("second-process", 2, roomId: 2));
    }

    private static RoomDetailResponse Room(int round, int version, bool facts = false)
    {
        var room = new RoomDetailResponse
        {
            RoomId = 1, RunSequence = 1, RoundNumber = round, RoomVersion = version,
            MonsterId = 8, MonsterHp = 500, MonsterMaxHp = 500,
            Slots = [new() { SlotIndex = 1, CharacterId = 1, IsOccupied = true, CharacterHp = 100, CharacterMaxHp = 100 }]
        };
        if (facts) room.BattleEvents.Add(new()
        {
            Id = 1, Sequence = 1, RoomId = 1, RunSequence = 1, RoundNumber = round,
            MonsterId = 8, SettlementVersion = version, Kind = BattleEventKind.Damage,
            Source = new("Character", 1), Target = new("Monster", 8), ActualAmount = 20
        });
        return room;
    }
}
