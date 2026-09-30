using Game.Server.Services;
using Game.Shared.Dtos;
using Xunit;

namespace Game.Server.Tests;

public sealed class RoomProjectionCacheTests
{
    [Fact]
    public void ProjectionCacheIsolatesRoomUserAndActiveCharacterKeys()
    {
        var revision = new RoomProjectionRevision();
        using var cache = new RoomProjectionCache(revision);
        var key = new RoomProjectionCache.Key(1, 10, 100);
        var id = cache.Store(key, Detail(), 0, revision.Value);

        Assert.NotEmpty(id);
        Assert.Equal(id, cache.Get(key, 9, 0)!.Value.Id);
        Assert.Null(cache.Get(new(2, 10, 100), 9, 0));
        Assert.Null(cache.Get(new(1, 20, 100), 9, 0));
        Assert.Null(cache.Get(new(1, 10, 200), 9, 0));
        Assert.Null(cache.Get(new(1, 10, null), 9, 0));
    }

    [Fact]
    public void ProjectionCacheReturnsIndependentNestedCopiesAndNeverCachesHistory()
    {
        var revision = new RoomProjectionRevision();
        using var cache = new RoomProjectionCache(revision);
        var key = new RoomProjectionCache.Key(1, 10, 100);
        var original = Detail();
        cache.Store(key, original, 0, revision.Value);
        original.Slots[0].Skills[0].SkillCode = "local edit";
        original.MonsterIntent!.TargetLabel = "local target";

        var first = cache.Get(key, 9, 0)!.Value.Room;
        Assert.Equal("knight-strike", first.Slots[0].Skills[0].SkillCode);
        Assert.Equal("front", first.MonsterIntent!.TargetLabel);
        Assert.Empty(first.BattleEvents);
        Assert.Empty(first.BattleLogs);
        Assert.Single(original.BattleEvents);
        Assert.Single(original.BattleLogs);
        first.Slots[0].Skills.Clear();
        first.MonsterIntent.TargetLabel = "another edit";
        first.BattleLogs.Add(new() { Text = "caller history" });

        var second = cache.Get(key, 9, 0)!.Value.Room;
        Assert.Equal("knight-strike", Assert.Single(second.Slots[0].Skills).SkillCode);
        Assert.Equal("front", second.MonsterIntent!.TargetLabel);
        Assert.Empty(second.BattleLogs);
    }

    [Fact]
    public void ProjectionCacheRejectsDifferentVersionOfflineMaskAndGlobalRevision()
    {
        var revision = new RoomProjectionRevision();
        using var cache = new RoomProjectionCache(revision);
        var key = new RoomProjectionCache.Key(1, 10, 100);
        cache.Store(key, Detail(), 0, revision.Value);

        Assert.Null(cache.Get(key, 10, 0));
        Assert.Null(cache.Get(key, 9, 1 << 2));
        Assert.NotNull(cache.Get(key, 9, 0));
        revision.Changed();
        Assert.Null(cache.Get(key, 9, 0));
    }

    [Fact]
    public void ProjectionBuiltUnderAnOlderRevisionIsNeverStored()
    {
        var revision = new RoomProjectionRevision();
        using var cache = new RoomProjectionCache(revision);
        var key = new RoomProjectionCache.Key(1, 10, 100);
        var captured = revision.Value;
        revision.Changed();

        Assert.Empty(cache.Store(key, Detail(), 0, captured));
        Assert.Null(cache.Get(key, 9, 0));
        Assert.NotEmpty(cache.Store(key, Detail(), 0, revision.Value));
    }

    private static RoomDetailResponse Detail() => new()
    {
        RoomId = 1, RoomVersion = 9, BattleHistoryEpoch = "epoch",
        Slots = [new RoomSlotResponse
        {
            SlotIndex = 1, CharacterId = 100, Skills = [new RoomSkillSlotResponse { SkillCode = "knight-strike" }]
        }],
        MonsterIntent = new() { TargetLabel = "front" },
        BattleEvents = [new() { Id = 1, Target = new("Monster", 1) }],
        BattleLogs = [new() { Id = 1, Text = "published history" }]
    };
}
