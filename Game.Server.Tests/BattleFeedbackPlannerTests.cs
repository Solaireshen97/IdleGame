using Game.Client.Services;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Xunit;

namespace Game.Server.Tests;

public class BattleFeedbackPlannerTests
{
    [Fact]
    public void ReplaysCommittedFactsInSequenceWithoutUsingTextOrActorNames()
    {
        var before = Room(4);
        var after = Room(5, 440);
        before.Slots[1].CharacterName = before.Slots[0].CharacterName;
        var old = Hit(1, Enemy, 99) with { Id = 100, RoundNumber = 4 };
        before.BattleEvents.Add(old); after.BattleEvents.Add(old);
        Add(after, Hit(1, Enemy, 30) with { IsCritical = true, ActionKind = BattleActionKind.NormalAttack },
            Hit(2, Enemy, 30) with { Source = Actor(2, "MAGE"), Label = "改名后的技能" },
            Hit(0, Actor(1), 12) with { Source = Enemy, ActionKind = BattleActionKind.NormalAttack });
        after.BattleEvents.Reverse();
        after.BattleLogs.Add(new() { Id = 999, Text = "2号位 星 攻击 史莱姆，造成 999 点伤害（暴击）。" });
        var plan = BattleFeedbackPlanner.Create(before, after)!;
        Assert.Equal(new[] { "1", "2", "enemy" }, plan.Events.Select(e => e.Source));
        Assert.Equal(new[] { "enemy", "enemy", "1" }, plan.Events.Select(e => e.Target));
        Assert.Equal(60, plan.TotalDamage); Assert.Equal(2, plan.HitCount); Assert.Equal(1, plan.CriticalCount);
        Assert.Equal("magic", plan.Events[1].Style); Assert.Equal("改名后的技能", plan.Events[1].Label);
    }

    [Theory]
    [InlineData("SWORDSMAN", "slash")]
    [InlineData("KNIGHT", "slash")]
    [InlineData("mage", "magic")]
    [InlineData("ACOLYTE", "holy")]
    [InlineData("CLERIC", "holy")]
    [InlineData("HUNTER", "arrow")]
    [InlineData("ROGUE", "dagger")]
    [InlineData("future-role", "slash")]
    public void StyleUsesStableProfessionCode(string code, string style)
    {
        var after = Room(1); Add(after, Hit(1, Enemy, 10) with { Source = Actor(1, code) });
        Assert.Equal(style, BattleFeedbackPlanner.Create(Room(0), after)!.Events.Single().Style);
    }

    [Fact]
    public void MissingEventsNeverFabricateAttackerDamageCriticalOrVictoryFromLogs()
    {
        var after = Room(1, 0); after.RoomStatus = RoomStatus.BattleOver;
        after.BattleLogs.Add(new() { Id = 1, Text = "史莱姆 已被击败。" });
        Assert.Null(BattleFeedbackPlanner.Create(Room(0), after));
        Assert.Null(BattleFeedbackPlanner.Create(Room(0), Room(1, 420)));
    }

    [Fact]
    public void IgnoresInitialLoadDuplicatesSkippedRoundsRestartsClosedRoomsAndWrongFactScope()
    {
        var after = Room(1); Add(after, Hit(1, Enemy, 10));
        Assert.Null(BattleFeedbackPlanner.Create(null, after));
        Assert.Null(BattleFeedbackPlanner.Create(Room(1), after));
        Assert.Null(BattleFeedbackPlanner.Create(Room(3), after));
        after.RunSequence++; Assert.Null(BattleFeedbackPlanner.Create(Room(0), after));
        after.RunSequence--; after.ClosedAtUtc = DateTime.UtcNow; Assert.Null(BattleFeedbackPlanner.Create(Room(0), after));
        after.ClosedAtUtc = null;
        var original = after.BattleEvents[0];
        foreach (var invalid in new[] { original with { RoomId = 2 }, original with { RunSequence = 2 },
            original with { RoundNumber = 2 }, original with { SettlementVersion = 5 }, original with { MonsterId = 9 }, original with { Target = new("Monster", 9, Name: "史莱姆") } })
        { after.BattleEvents = [invalid]; Assert.Null(BattleFeedbackPlanner.Create(Room(0), after)); }
    }

    [Fact]
    public void UnknownSourceDoesNotMatchByNameAndTargetSlotIsResolvedByIdentity()
    {
        var after = Room(1);
        Add(after, Hit(1, Actor(2) with { SlotIndex = 1 }, 10) with { Source = new("Character", 999, 1, "岚", "MAGE"), IsCritical = false });
        var hit = BattleFeedbackPlanner.Create(Room(0), after)!.Events.Single();
        Assert.Equal("", hit.Source); Assert.Equal("2", hit.Target); Assert.Equal("pulse", hit.Style); Assert.False(hit.Critical);
    }

    [Fact]
    public void OverkillKeepsActualLossAndRecordedHealthInsteadOfCalculatedDamage()
    {
        var after = Room(1, 0); after.RoomStatus = RoomStatus.BattleOver;
        Add(after, Hit(1, Enemy, 500) with { CalculatedAmount = 700, HpBefore = 500, HpAfter = 0, TargetMaxHp = 500 }, Defeat());
        var plan = BattleFeedbackPlanner.Create(Room(0), after)!;
        Assert.True(plan.Defeated); Assert.True(plan.Victory); Assert.Equal(500, plan.TotalDamage);
        Assert.Equal(0, plan.Events.Single().HpAfter); Assert.Equal(500, plan.Events.Single().TargetMaxHp);
        Assert.Equal(0, plan.FinalVitals["enemy"].Hp);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public void KillingRoundUsesOldMonsterIdentityEvenWhenNextMonsterHasSameName(bool nextWave, bool sameName)
    {
        var before = Room(4, 12); var after = Room(5, 900);
        after.MonsterId = 9; after.MonsterName = sameName ? before.MonsterName : "史莱姆王"; after.MonsterMaxHp = 900;
        if (nextWave) after.CurrentWaveNumber++; else after.CurrentEnemyNumber++;
        after.RoomStatus = RoomStatus.WaveTransition;
        Add(after, Hit(2, Enemy, 12), Defeat());
        var plan = BattleFeedbackPlanner.Create(before, after)!;
        Assert.True(plan.Defeated); Assert.False(plan.Victory); Assert.Equal(new BattleFeedbackVitals(0, 500), plan.FinalVitals["enemy"]);
        Assert.True(BattleFeedbackPlanner.CanFinishPlayback(before, after, plan));
        after.RoomStatus = RoomStatus.NotStarted; Assert.True(BattleFeedbackPlanner.CanFinishPlayback(before, after, plan));
        after.RoundNumber++; Assert.False(BattleFeedbackPlanner.CanFinishPlayback(before, after, plan));
    }

    [Fact]
    public void SameNameDoesNotMakeDifferentMonsterTheSameEncounter()
    {
        var before = Room(0); var after = Room(1); after.MonsterId++;
        Add(after, Hit(1, Enemy, 10));
        Assert.False(BattleFeedbackPlanner.SameEncounter(before, after)); Assert.Null(BattleFeedbackPlanner.Create(before, after));
        before.BattleEvents.Add(Defeat() with { Id = 500 }); after.BattleEvents.Add(before.BattleEvents[0]);
        Assert.Null(BattleFeedbackPlanner.Create(before, after));
    }

    [Fact]
    public void ClosedFinalKillCanFinishUntilAnotherRoundOrRunStarts()
    {
        var before = Room(0, 10); var after = Room(1, 0);
        after.RoomStatus = RoomStatus.BattleOver; after.ClosedAtUtc = DateTime.UtcNow;
        Add(after, Hit(1, Enemy, 10), Defeat()); var plan = BattleFeedbackPlanner.Create(before, after)!;
        Assert.True(plan.Victory); Assert.True(BattleFeedbackPlanner.CanFinishPlayback(before, after, plan));
        after.RunSequence++; Assert.False(BattleFeedbackPlanner.CanFinishPlayback(before, after, plan));
    }

    [Fact]
    public void LongChainsKeepEveryAmountElementCriticalAndExplicitSkillFlag()
    {
        var after = Room(1); Add(after, Enumerable.Range(0, 100).Select(i => Hit(i % 2 + 1, Enemy, i + 11) with
        { Element = i % 2 == 0 ? ElementType.Fire : ElementType.Water, ElementModifier = i % 2 == 0 ? 25 : -25, IsCritical = i % 3 == 0,
            ActionKind = i % 2 == 0 ? BattleActionKind.Skill : BattleActionKind.NormalAttack, Label = "普通攻击" }).ToArray());
        var plan = BattleFeedbackPlanner.Create(Room(0), after)!; Assert.Equal(100, plan.HitCount); Assert.Equal(34, plan.CriticalCount);
        for (var i = 0; i < 100; i++)
        { Assert.Equal(i + 11, plan.Events[i].Amount); Assert.Equal(i % 2 == 0 ? "fire" : "water", plan.Events[i].Tone);
            Assert.Equal(i % 2 == 0 ? 25 : -25, plan.Events[i].ElementModifierPercent); Assert.Equal(i % 2 == 0, plan.Events[i].IsSkill); }
    }

    [Theory]
    [InlineData(ElementType.Fire, 25, "fire")] [InlineData(ElementType.Water, -25, "water")]
    [InlineData(ElementType.Light, 0, "light")] [InlineData(ElementType.Dark, 25, "dark")]
    public void SoulUsesItsExplicitElementAndSkillKind(ElementType element, int modifier, string tone)
    {
        var after = Room(1); Add(after, Hit(1, Enemy, 32) with { ActionKind = BattleActionKind.SoulImprint, Element = element, ElementModifier = modifier });
        var hit = BattleFeedbackPlanner.Create(Room(0), after)!.Events.Single();
        Assert.Equal(tone, hit.Tone); Assert.Equal(modifier, hit.ElementModifierPercent); Assert.Equal("magic", hit.Style); Assert.True(hit.IsSkill);
    }

    [Fact]
    public void PeriodicAndFixedDamageNeverInferWeaponAffinity()
    {
        var before = Room(0); before.Slots[0].CharacterElement = ElementType.Fire; before.Slots[0].OutgoingElementModifierPercent = 25;
        var after = Room(1); Add(after, Hit(1, Enemy, 9) with { ActionKind = BattleActionKind.Counter },
            Hit(0, Enemy, 8) with { Source = null, ActionKind = BattleActionKind.Periodic });
        var events = BattleFeedbackPlanner.Create(before, after)!.Events;
        Assert.All(events, hit => { Assert.Equal("neutral", hit.Tone); Assert.Equal(0, hit.ElementModifierPercent); Assert.False(hit.IsSkill); });
        Assert.Equal("pulse", events[1].Style);
    }

    [Theory]
    [InlineData(BattleActionKind.NormalAttack, 1, true)] [InlineData(BattleActionKind.Skill, 1, false)]
    [InlineData(BattleActionKind.NormalAttack, 2, false)] [InlineData(BattleActionKind.Periodic, 1, false)]
    public void OnlyAdjacentNormalEchoFromSameActorSharesLaunch(BattleActionKind preceding, int source, bool paired)
    {
        var after = Room(1); Add(after, Hit(source, Enemy, 50) with { ActionKind = preceding },
            Hit(1, Enemy, 25) with { ActionKind = BattleActionKind.FollowUp, SkillCode = "normal-echo", HpAfter = 425 });
        var plan = BattleFeedbackPlanner.Create(Room(0), after)!;
        Assert.Equal(paired, plan.Events[1].IsFollowUp); Assert.Equal(75, plan.TotalDamage); Assert.Equal(2, plan.HitCount);
    }

    [Fact]
    public void UtilityAndTransientStatusesKeepOrderedSnapshotsWithoutDamageOrFinalStateLookup()
    {
        var before = Room(0); before.Slots[0].StatusEffects.Add(State(1).ToResponse());
        var after = Room(1);
        var added = State(3); var consumed = State(2);
        Add(after, new() { Kind = BattleEventKind.Status, Target = Actor(1), Status = added, StatusChange = BattleStatusChange.Refreshed, CountBefore = 1, CountAfter = 3, Label = added.Name },
            new() { Kind = BattleEventKind.Status, Target = Actor(1), Status = consumed, StatusChange = BattleStatusChange.Consumed, CountBefore = 3, CountAfter = 2, Label = consumed.Name },
            new() { Kind = BattleEventKind.Status, Target = Actor(1), Status = consumed, StatusChange = BattleStatusChange.Expired, CountBefore = 2, CountAfter = 0, Label = consumed.Name },
            new() { Kind = BattleEventKind.Cleanse, Source = Actor(1), Target = Actor(2), Status = State(1) with { IsPositive = false }, StatusChange = BattleStatusChange.Removed, CountAfter = 0 },
            new() { Kind = BattleEventKind.Dispel, Source = Actor(2), Target = Enemy, Status = State(1), StatusChange = BattleStatusChange.Removed, CountAfter = 0 },
            new() { Kind = BattleEventKind.Cooldown, Source = Actor(1), Target = Actor(1), ActualAmount = 1, Label = "冷却缩短 1 回合" },
            new() { Kind = BattleEventKind.Interrupt, Source = Actor(2), Target = Enemy, Label = "打断" });
        var plan = BattleFeedbackPlanner.Create(before, after)!;
        Assert.Equal(new[] { "buff", "buff", "status", "cleanse", "dispel", "cooldown", "interrupt" }, plan.Events.Select(e => e.Kind));
        Assert.Equal(new int?[] { 3, 2, 0, 0, 0, null, null }, plan.Events.Select(e => e.CountAfter));
        Assert.Equal(3, plan.Events[0].Status!.Stacks); Assert.Equal("剩余 3 次", plan.Events[0].Status!.CounterText);
        Assert.Equal("初始描述", plan.Events[0].Status!.Description); Assert.Equal(0, plan.TotalDamage); Assert.Equal(0, plan.HitCount);
        before.Slots[0].StatusEffects[0].Stacks = 99; Assert.Equal(1, plan.InitialStatuses["1"].Single().Stacks);
        Assert.All(plan.Events, e => Assert.Equal(0, e.Amount));
    }

    [Fact]
    public void HealingAndMultipleEnemyTargetsUseActualIdsAndFrozenHealth()
    {
        var after = Room(1); Add(after, new() { Kind = BattleEventKind.Heal, Source = Actor(1), Target = Actor(2), ActualAmount = 4, CalculatedAmount = 20, HpAfter = 100 },
            Hit(0, Actor(1), 11) with { Source = Enemy }, Hit(0, Actor(2), 13) with { Source = Enemy });
        var plan = BattleFeedbackPlanner.Create(Room(0), after)!;
        Assert.Equal(new[] { "2", "1", "2" }, plan.Events.Select(e => e.Target)); Assert.Equal(4, plan.Events[0].Amount);
        Assert.Equal(100, plan.Events[0].HpAfter); Assert.Equal(0, plan.HitCount);
    }

    [Fact]
    public void SkillArtUsesFrozenCodesAndSharesWindupWithoutMergingFlurryHits()
    {
        var after = Room(1);
        Add(after, Hit(1, Enemy, 10) with { SkillCode = "rogue-blade-flurry", Source = Actor(1, "ROGUE") },
            Hit(0, Actor(1), 3) with { Source = Enemy, ActionKind = BattleActionKind.Counter },
            Hit(1, Enemy, 11) with { SkillCode = "rogue-blade-flurry", Source = Actor(1, "ROGUE") },
            Hit(1, Enemy, 12) with { SkillCode = "rogue-blade-flurry", Source = Actor(1, "ROGUE") });
        var plan = BattleFeedbackPlanner.Create(Room(0), after)!;
        var hits = plan.Events.Where(e => e.Source == "1").ToList();
        Assert.Equal(3, hits.Count); Assert.Equal(33, plan.TotalDamage); Assert.Equal(3, plan.HitCount);
        Assert.Single(hits.Select(e => e.CastKey).Distinct());
        Assert.All(hits, e => { Assert.Equal("rogue-blade-flurry", e.SkillCode); Assert.Equal("ROGUE", e.ProfessionCode); Assert.NotNull(e.CastKey); });
        Assert.Null(plan.Events[1].CastKey);
    }

    [Fact]
    public void GroupUtilityKeepsEveryTargetButPeriodicAndConsumedFactsDoNotRecast()
    {
        var after = Room(1);
        Add(after, new() { Kind = BattleEventKind.Status, Source = Actor(1), Target = Actor(1), SkillCode = "knight-faith-barrier", Status = State(1) with { EffectType = "Guard" }, StatusChange = BattleStatusChange.Added },
            new() { Kind = BattleEventKind.Status, Source = Actor(1), Target = Actor(2), SkillCode = "knight-faith-barrier", Status = State(1) with { EffectType = "Guard" }, StatusChange = BattleStatusChange.Added },
            new() { Kind = BattleEventKind.Status, Source = Actor(1), Target = Actor(1), SkillCode = "knight-faith-barrier", Status = State(1), StatusChange = BattleStatusChange.Consumed },
            new() { Kind = BattleEventKind.Heal, Source = Actor(2, "CLERIC"), Target = Actor(1), SkillCode = "acolyte-group-heal", ActualAmount = 5 },
            new() { Kind = BattleEventKind.Heal, Source = Actor(2, "CLERIC"), Target = Actor(2), SkillCode = "acolyte-group-heal", ActualAmount = 6 },
            new() { Kind = BattleEventKind.Heal, Source = Actor(2, "CLERIC"), Target = Actor(1), SkillCode = "acolyte-group-heal", ActualAmount = 2, ActionKind = BattleActionKind.Periodic });
        var events = BattleFeedbackPlanner.Create(Room(0), after)!.Events;
        Assert.Equal(events[0].CastKey, events[1].CastKey); Assert.NotNull(events[0].CastKey);
        Assert.Equal("Guard", events[0].StatusEffectType); Assert.NotEqual(events[0].Target, events[1].Target);
        Assert.Equal(events[3].CastKey, events[4].CastKey); Assert.True(events[3].IsSkill);
        Assert.False(events[2].IsSkill); Assert.Null(events[2].CastKey);
        Assert.False(events[5].IsSkill); Assert.Null(events[5].CastKey);
    }

    private static BattleEventActor Actor(int id, string code = "SWORDSMAN") => new("Character", id, id, "同名", code);
    private static readonly BattleEventActor Enemy = new("Monster", 8, Name: "史莱姆");
    private static BattleEventResponse Hit(int source, BattleEventActor target, int amount) => new()
    { Kind = BattleEventKind.Damage, Source = source == 0 ? null : Actor(source), Target = target, ActualAmount = amount, CalculatedAmount = amount, Label = "动作" };
    private static BattleEventResponse Defeat() => new() { Kind = BattleEventKind.Defeat, Target = Enemy };
    private static BattleStatusSnapshot State(int count) => new()
    { Code = "charges", Name = "同名状态", Description = "初始描述", IsPositive = true, Stacks = count, CounterKind = BattleStatusCounterKind.Charges,
        CounterText = $"剩余 {count} 次", DurationText = "消耗后移除", Lifetime = BattleStatusLifetime.UntilConsumed, BoundTargetName = "史莱姆" };
    private static void Add(RoomDetailResponse room, params BattleEventResponse[] facts)
    {
        foreach (var fact in facts) room.BattleEvents.Add(fact with { Id = room.BattleEvents.Select(e => e.Id).DefaultIfEmpty(100).Max() + 1,
            Sequence = room.BattleEvents.Count + 1, RoomId = room.RoomId, RunSequence = room.RunSequence, RoundNumber = room.RoundNumber, MonsterId = 8, SettlementVersion = 4 });
    }
    private static RoomDetailResponse Room(int round, int monsterHp = 500) => new()
    {
        RoomId = 1, RoomVersion = 4, RunSequence = 1, RoundNumber = round, MonsterId = 8, MonsterName = "史莱姆", MonsterHp = monsterHp, MonsterMaxHp = 500,
        CurrentWaveNumber = 1, CurrentEnemyNumber = 1,
        Slots = [new() { SlotIndex = 1, CharacterId = 1, CharacterName = "岚", ProfessionName = "剑士", CharacterHp = 100, CharacterMaxHp = 100, IsOccupied = true, IsAlive = true },
            new() { SlotIndex = 2, CharacterId = 2, CharacterName = "星", ProfessionName = "法师", CharacterHp = 100, CharacterMaxHp = 100, IsOccupied = true, IsAlive = true }]
    };
}
