using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Models;
using Xunit;

namespace Game.Server.Tests;

public sealed class BattleStatisticsReducerTests
{
    [Theory]
    [InlineData(BattleEventKind.Cleanse)]
    [InlineData(BattleEventKind.Dispel)]
    public void RemovalWithoutActionScopeDoesNotCreditOriginalStatusProvider(BattleEventKind kind)
    {
        var collector = new BattleEventCollector();
        var room = new Room { Id = 1, RunSequence = 2, RoundNumber = 2, Version = 4 };
        var monster = new Monster { Id = 5, Name = "enemy" };
        using var recording = collector.Begin(room, monster, []);
        collector.Status(room, "Character", 9,
            new() { Code = "buff", Name = "buff", Description = "", SourceActorType = "Character",
                SourceActorId = 8, SourceSkillCode = "original-provider" },
            BattleStatusChange.Removed, 1, 0, kind);
        var fact = Assert.Single(collector.Snapshot(room));
        Assert.Null(fact.Source);
        Assert.Equal(8, fact.Status!.SourceActorId);
        var result = BattleStatisticsReducer.Reduce(Batch(fact));
        Assert.Empty(result.Actors);
    }

    private static BattleEventActor Character(int id) => new("Character", id);
    private static BattleEventResponse Fact(int sequence, BattleEventKind kind = BattleEventKind.Damage,
        int actual = 7, BattleEventActor? source = null, BattleEventActor? target = null,
        string? code = "strike", BattleActionKind action = BattleActionKind.Skill) => new()
    {
        RoomId = 1, RunSequence = 2, RoundNumber = 3, SettlementVersion = 4, MonsterId = 5,
        Sequence = sequence, Kind = kind, ActualAmount = actual, CalculatedAmount = 100,
        Source = source, Target = target ?? new("Monster", 5), SkillCode = code, ActionKind = action
    };

    private static BattleSettlementSnapshot Batch(params BattleEventResponse[] events) => new()
    {
        RoomId = 1, RunSequence = 2, RoundNumber = 3, SettlementVersion = 4,
        Enemy = new(5, "enemy", 1, 1, false), Events = events
    };

    [Fact]
    public void ActualDamageIncludesEveryHitAndEverySourceDetailExactlyOnce()
    {
        var result = BattleStatisticsReducer.Reduce(Batch(
            Fact(1, source: Character(8)), Fact(2, source: Character(8)),
            Fact(3, actual: 2, source: Character(8), code: "poison", action: BattleActionKind.Periodic)));
        var actor = Assert.Single(result.Actors);
        Assert.Equal(16L, actor.DamageDealt);
        Assert.Equal(2, result.Abilities.Count);
        Assert.Equal(14L, Assert.Single(result.Abilities, a => a.SourceCode == "strike").DamageDealt);
        Assert.Equal(actor.DamageDealt, result.Abilities.Sum(a => a.DamageDealt));
        // A departed DoT owner retains its authoritative identity without inventing roster metadata.
        Assert.Equal(8, actor.CharacterId); Assert.Null(actor.UserId); Assert.Null(actor.SlotIndex);
        Assert.Equal("", actor.Name); Assert.Equal(0L, actor.PresentRounds);
    }

    [Fact]
    public void EmptySettlementStillCountsPresentAndLivingRosterMembers()
    {
        var batch = Batch() with { Participants = [
            new(8, 80, "alive", "mage", null, 1, 10, new()),
            new(9, 90, "dead", "knight", null, 2, 0, new())] };
        var result = BattleStatisticsReducer.Reduce(batch);
        Assert.Equal(1L, result.Encounter.RecordedRounds); Assert.Empty(result.Abilities);
        Assert.All(result.Actors, a => { Assert.Equal(1L, a.PresentRounds); Assert.Equal(0L, a.DamageDealt); });
        Assert.Equal(1L, Assert.Single(result.Actors, a => a.CharacterId == 8).AliveRounds);
        Assert.Equal(0L, Assert.Single(result.Actors, a => a.CharacterId == 9).AliveRounds);
    }

    [Fact]
    public void DeathCountsOnlyPositiveHealthTransitionAndNotDefeatNoticeOrZeroHit()
    {
        var result = BattleStatisticsReducer.Reduce(Batch(
            Fact(1, actual: 4, target: Character(8)) with { HpBefore = 4, HpAfter = 0 },
            Fact(2, actual: 0, target: Character(8)) with { HpBefore = 0, HpAfter = 0 },
            Fact(3, BattleEventKind.Defeat, actual: 0, target: Character(8))));
        var actor = Assert.Single(result.Actors);
        Assert.Equal(4L, actor.DamageTaken); Assert.Equal(1L, actor.Deaths); Assert.False(actor.WasAlive);
    }

    [Fact]
    public void EffectiveHealingSeparatesPotionsSelfAndTeammatesWithoutDoubleCountingTotals()
    {
        var result = BattleStatisticsReducer.Reduce(Batch(
            Fact(1, BattleEventKind.Heal, 1, Character(8), Character(8), "potion", BattleActionKind.Consumable),
            Fact(2, BattleEventKind.Heal, 3, Character(8), Character(8), "renew"),
            Fact(3, BattleEventKind.Heal, 5, Character(8), Character(9), "renew")));
        var healer = Assert.Single(result.Actors, a => a.CharacterId == 8);
        Assert.Equal(9L, healer.HealingDone); Assert.Equal(4L, healer.HealingReceived);
        Assert.Equal(4L, healer.SelfHealing); Assert.Equal(1L, healer.PotionHealing);
        Assert.Equal(5L, Assert.Single(result.Actors, a => a.CharacterId == 9).HealingReceived);
        Assert.Equal(healer.HealingDone, result.Abilities.Sum(a => a.HealingDone));
        Assert.Equal(healer.SelfHealing, result.Abilities.Sum(a => a.SelfHealing));
        Assert.Equal(healer.PotionHealing, result.Abilities.Sum(a => a.PotionHealing));
    }

    [Fact]
    public void SuccessfulRemovalCountsSupportButExpiryAndRetainedStatusesDoNot()
    {
        var result = BattleStatisticsReducer.Reduce(Batch(
            Fact(1, BattleEventKind.Cleanse, 0, Character(8), Character(9)) with { StatusChange = BattleStatusChange.Removed },
            Fact(2, BattleEventKind.Dispel, 0, Character(8)) with { StatusChange = BattleStatusChange.Removed },
            Fact(3, BattleEventKind.Cleanse, 0, Character(8)) with { StatusChange = BattleStatusChange.Expired },
            Fact(4, BattleEventKind.Dispel, 0, Character(8)) with { StatusChange = BattleStatusChange.Retained },
            Fact(5, BattleEventKind.Status, 0, Character(8)) with { StatusChange = BattleStatusChange.Expired },
            Fact(6, BattleEventKind.Interrupt, 0, Character(8))));
        var actor = Assert.Single(result.Actors);
        Assert.Equal(1L, actor.Cleanses); Assert.Equal(1L, actor.Dispels); Assert.Equal(1L, actor.Interrupts);
    }

    [Fact]
    public void MissingOwnerUsesEncounterUnknownBucketWhileMissingSkillUsesAbilityUnknownBucket()
    {
        var result = BattleStatisticsReducer.Reduce(Batch(Fact(1, actual: 13),
            Fact(2, BattleEventKind.Heal, 11, target: Character(9)),
            Fact(3, actual: 17, source: Character(8), code: null)));
        Assert.Equal(13L, result.Encounter.UnattributedDamage); Assert.Equal(11L, result.Encounter.UnattributedHealing);
        Assert.Equal("unknown", Assert.Single(result.Abilities).SourceCode);
        Assert.DoesNotContain(result.Actors, a => a.CharacterId <= 0);
    }

    [Fact]
    public void AmountsAccumulateBeyondInt32()
    {
        var result = BattleStatisticsReducer.Reduce(Batch(Fact(1, actual: int.MaxValue, source: Character(8)),
            Fact(2, actual: int.MaxValue, source: Character(8))));
        Assert.Equal(2L * int.MaxValue, Assert.Single(result.Actors).DamageDealt);
        Assert.Equal(2L * int.MaxValue, Assert.Single(result.Abilities).DamageDealt);
    }

    [Theory]
    [InlineData("room")]
    [InlineData("run")]
    [InlineData("round")]
    [InlineData("monster")]
    [InlineData("version")]
    [InlineData("sequence")]
    public void MixedSettlementIdentityIsRejected(string field)
    {
        var fact = Fact(1, source: Character(8));
        fact = field switch {
            "room" => fact with { RoomId = 2 }, "run" => fact with { RunSequence = 3 },
            "round" => fact with { RoundNumber = 4 }, "monster" => fact with { MonsterId = 6 },
            "version" => fact with { SettlementVersion = 5 }, _ => fact with { Sequence = 0 }
        };
        Assert.Throws<ArgumentException>(() => BattleStatisticsReducer.Reduce(Batch(fact)));
    }

    [Fact]
    public void DuplicateEventSequenceAndRosterIdentityAreRejected()
    {
        Assert.Throws<ArgumentException>(() => BattleStatisticsReducer.Reduce(Batch(Fact(1), Fact(1))));
        var participant = new BattleStatisticsParticipantSnapshot(8, 80, "name", "mage", null, 1, 10, new());
        Assert.Throws<ArgumentException>(() => BattleStatisticsReducer.Reduce(Batch() with { Participants = [participant, participant] }));
    }
}
