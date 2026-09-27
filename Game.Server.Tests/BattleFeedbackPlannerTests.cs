using Game.Client.Services;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Xunit;

namespace Game.Server.Tests;

public class BattleFeedbackPlannerTests
{
    [Fact]
    public void PlaysOnlyNewHitsInServerOrderAndKeepsCriticalAndCounterattackSeparate()
    {
        var before = Room(4);
        before.BattleLogs.Add(new() { Id = 100, Text = "1号位 岚 普通攻击 史莱姆，造成 99 点伤害。" });
        var after = Room(5, 440);
        after.BattleLogs.Add(before.BattleLogs[0]);
        AddLogs(after, "1号位 岚 普通攻击 史莱姆，造成 30 点伤害（暴击）。",
            "2号位 星 使用 火球术 攻击 史莱姆，造成 30 点伤害。",
            "史莱姆 普通攻击 1号位 岚，造成 12 点伤害。");
        var plan = Assert.IsType<BattleFeedbackPlan>(BattleFeedbackPlanner.Create(before, after));
        Assert.Equal(new[] { "1", "2", "enemy" }, plan.Events.Select(e => e.Source));
        Assert.Equal(new[] { "enemy", "enemy", "1" }, plan.Events.Select(e => e.Target));
        Assert.Equal(60, plan.TotalDamage);
        Assert.Equal(2, plan.HitCount);
        Assert.Equal(1, plan.CriticalCount);
        Assert.Equal("magic", plan.Events[1].Style);
        Assert.Equal("火球术", plan.Events[1].Label);
    }

    [Fact]
    public void HealingGuardsInterruptsAndDotsTargetTheCorrectUnit()
    {
        var before = Room(0);
        var after = Room(1);
        AddLogs(after, "1号位 岚 使用 治疗术，为 2号位 星 恢复 24 点生命值。",
            "1号位 岚 使用 援护，守护 2号位 星。",
            "1号位 岚 使用 盾击，打断了 史莱姆 的行动。",
            "2号位 星 受到 中毒 造成的 5 点伤害。",
            "史莱姆 受到 灼烧 造成的 8 点伤害。");
        var plan = BattleFeedbackPlanner.Create(before, after)!;
        Assert.Equal(new[] { "heal", "guard", "interrupt", "damage", "damage" }, plan.Events.Select(e => e.Kind));
        Assert.Equal(new[] { "2", "2", "enemy", "2", "enemy" }, plan.Events.Select(e => e.Target));
        Assert.Equal("", plan.Events[3].Source);
        Assert.Equal(8, plan.TotalDamage);
    }

    [Fact]
    public void MultiTargetEnemySkillsAndSoulEchoesKeepTheirActualAmounts()
    {
        var before = Room(0);
        var after = Room(1, 370);
        AddLogs(after, "1号位 岚 释放魂印「焰心」攻击 史莱姆，造成 100 点火属性伤害（暴击）。",
            "魂印毒蚀对 史莱姆 追加 30 点无视防御伤害。",
            "史莱姆 使用 腐蚀喷射。",
            "史莱姆 使用 腐蚀喷射 攻击 1号位 岚，造成 11 点伤害。",
            "史莱姆 使用 腐蚀喷射 攻击 2号位 星，造成 13 点伤害。");
        var plan = BattleFeedbackPlanner.Create(before, after)!;
        Assert.Equal(4, plan.Events.Count);
        Assert.Equal(130, plan.TotalDamage);
        Assert.Equal("焰心", plan.Events[0].Label);
        Assert.Equal("magic", plan.Events[0].Style);
        Assert.Equal(13, plan.Events[3].Amount);
        Assert.Equal("2", plan.Events[3].Target);
    }

    [Theory]
    [InlineData("猎人", "arrow")]
    [InlineData("神射手", "arrow")]
    [InlineData("刺客", "dagger")]
    [InlineData("奥术师", "magic")]
    [InlineData("祭司", "holy")]
    [InlineData("牧师", "holy")]
    [InlineData("审判官", "holy")]
    [InlineData("骑士", "slash")]
    public void ProfessionDeterminesAttackPresentation(string profession, string style)
    {
        var before = Room(0);
        before.Slots[0].ProfessionName = profession;
        var after = Room(1);
        AddLogs(after, "1号位 岚 普通攻击 史莱姆，造成 10 点伤害。");
        Assert.Equal(style, BattleFeedbackPlanner.Create(before, after)!.Events.Single().Style);
    }

    [Fact]
    public void DoesNotReplayOnInitialLoadDuplicatePollSkippedRoundsResetOrNewEncounter()
    {
        Assert.Null(BattleFeedbackPlanner.Create(null, Room(1)));
        Assert.Null(BattleFeedbackPlanner.Create(Room(1), Room(1)));
        Assert.Null(BattleFeedbackPlanner.Create(Room(1), Room(3)));
        Assert.Null(BattleFeedbackPlanner.Create(Room(1), Room(0)));
        var before = Room(1);
        var nextRun = Room(2); nextRun.RunSequence++;
        var nextEnemy = Room(2); nextEnemy.CurrentEnemyNumber++;
        var closed = Room(2); closed.ClosedAtUtc = DateTime.UtcNow;
        Assert.Null(BattleFeedbackPlanner.Create(before, nextRun));
        Assert.Null(BattleFeedbackPlanner.Create(before, nextEnemy));
        Assert.Null(BattleFeedbackPlanner.Create(before, closed));
    }

    [Fact]
    public void MissingLogsUseHpDeltaWithoutInventingAnAttackerOrCritical()
    {
        var plan = BattleFeedbackPlanner.Create(Room(0), Room(1, 420))!;
        Assert.Equal(80, plan.TotalDamage);
        Assert.Equal("", plan.Events.Single().Source);
        Assert.False(plan.Events.Single().Critical);
    }

    [Fact]
    public void OverkillUsesLoggedDamageButFinalHealthAndVictoryComeFromServer()
    {
        var after = Room(1, 0);
        after.RoomStatus = RoomStatus.BattleOver;
        AddLogs(after, "1号位 岚 普通攻击 史莱姆，造成 700 点伤害。");
        var plan = BattleFeedbackPlanner.Create(Room(0), after)!;
        Assert.True(plan.Defeated);
        Assert.True(plan.Victory);
        Assert.Equal(700, plan.TotalDamage);
        Assert.Equal(0, plan.FinalVitals["enemy"].Hp);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void KillingRoundPlaysAgainstOldEnemyWhenServerAlreadySelectsNext(bool nextWave, bool sameName)
    {
        var before = Room(4, 12);
        var after = Room(5, 900);
        after.MonsterName = sameName ? before.MonsterName : "史莱姆王";
        after.MonsterMaxHp = 900;
        if (nextWave) after.CurrentWaveNumber++;
        else after.CurrentEnemyNumber++;
        after.RoomStatus = RoomStatus.WaveTransition;
        AddLogs(after, "2号位 星 使用 火球术 攻击 史莱姆，造成 35 点伤害（暴击）。", "史莱姆 已被击败。");
        var plan = Assert.IsType<BattleFeedbackPlan>(BattleFeedbackPlanner.Create(before, after));
        Assert.True(plan.Defeated);
        Assert.False(plan.Victory);
        Assert.Equal("2", plan.Events.Single().Source);
        Assert.Equal(35, plan.TotalDamage);
        Assert.Equal(new BattleFeedbackVitals(0, 500), plan.FinalVitals["enemy"]);
        // Countdown polls and the next monster becoming ready cannot cut the finale.
        Assert.True(BattleFeedbackPlanner.CanFinishPlayback(before, after, plan));
        after.RoomStatus = RoomStatus.NotStarted;
        Assert.True(BattleFeedbackPlanner.CanFinishPlayback(before, after, plan));
        after.RoundNumber++;
        Assert.False(BattleFeedbackPlanner.CanFinishPlayback(before, after, plan));
    }

    [Fact]
    public void FinalKillCanFinishBeforeExpiredRoomShowsItsRewards()
    {
        var before = Room(0, 10);
        var after = Room(1, 0);
        after.RoomStatus = RoomStatus.BattleOver;
        after.ClosedAtUtc = DateTime.UtcNow;
        AddLogs(after, "1号位 岚 普通攻击 史莱姆，造成 20 点伤害。", "史莱姆 已被击败。");
        var plan = Assert.IsType<BattleFeedbackPlan>(BattleFeedbackPlanner.Create(before, after));
        Assert.True(plan.Victory);
        Assert.True(BattleFeedbackPlanner.CanFinishPlayback(before, after, plan));
        after.RunSequence++;
        Assert.False(BattleFeedbackPlanner.CanFinishPlayback(before, after, plan));
    }

    [Fact]
    public void OldDefeatLogDoesNotReplayAgainstAnotherEnemy()
    {
        var before = Room(0);
        before.BattleLogs.Add(new() { Id = 100, Text = "史莱姆 已被击败。" });
        var after = Room(1);
        after.CurrentEnemyNumber++;
        after.BattleLogs.Add(before.BattleLogs[0]);
        Assert.Null(BattleFeedbackPlanner.Create(before, after));
    }

    [Fact]
    public void TenHitsKeepEveryAmountItsElementAndItsOwnAffinity()
    {
        var before = Room(0);
        before.Slots[0].CharacterElement = ElementType.Fire;
        before.Slots[0].OutgoingElementModifierPercent = 25;
        before.Slots[1].CharacterElement = ElementType.Water;
        before.Slots[1].OutgoingElementModifierPercent = -25;
        var after = Room(1, 345);
        AddLogs(after, Enumerable.Range(0, 10).Select(i =>
            $"{(i % 2 == 0 ? "1号位 岚" : "2号位 星")} 使用 连击 攻击 史莱姆，造成 {i + 11} 点伤害{(i % 3 == 0 ? "（暴击）" : "")}。").ToArray());
        var plan = BattleFeedbackPlanner.Create(before, after)!;
        Assert.Equal(10, plan.HitCount);
        Assert.Equal(Enumerable.Range(11, 10), plan.Events.Select(e => e.Amount));
        Assert.Equal(4, plan.CriticalCount);
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(i % 2 == 0 ? "fire" : "water", plan.Events[i].Tone);
            Assert.Equal(i % 2 == 0 ? 25 : -25, plan.Events[i].ElementModifierPercent);
        }
    }

    [Theory]
    [InlineData("火", ElementType.Wind, "fire", 25)]
    [InlineData("火", ElementType.Water, "fire", -25)]
    [InlineData("水", ElementType.Water, "water", 0)]
    [InlineData("光", ElementType.Dark, "light", 25)]
    [InlineData("暗", ElementType.Light, "dark", 25)]
    public void SoulDamageUsesItsLoggedElementInsteadOfTheWeapon(string element, ElementType target, string tone, int modifier)
    {
        var before = Room(0);
        before.MonsterElement = target;
        before.Slots[0].CharacterElement = ElementType.Earth;
        before.Slots[0].OutgoingElementModifierPercent = -25;
        var after = Room(1);
        AddLogs(after, $"1号位 岚 释放魂印「魂印」攻击 史莱姆，造成 32 点{element}属性伤害（暴击）。");
        var hit = BattleFeedbackPlanner.Create(before, after)!.Events.Single();
        Assert.Equal(tone, hit.Tone);
        Assert.Equal(modifier, hit.ElementModifierPercent);
        Assert.True(hit.Critical);
    }

    [Fact]
    public void FixedDamageDoesNotInventAnElementalAdvantage()
    {
        var before = Room(0);
        before.Slots[0].CharacterElement = ElementType.Fire;
        before.Slots[0].OutgoingElementModifierPercent = 25;
        var after = Room(1);
        AddLogs(after, "1号位 岚 的连绵攻势毒蚀对 史莱姆 造成 9 点无视防御伤害。",
            "史莱姆 受到 中毒 造成的 8 点伤害。", "1号位 岚 招架后反击 史莱姆，造成 7 点伤害。");
        Assert.All(BattleFeedbackPlanner.Create(before, after)!.Events, hit =>
        {
            Assert.Equal("neutral", hit.Tone);
            Assert.Equal(0, hit.ElementModifierPercent);
        });
    }

    [Theory]
    [InlineData("普通攻击")]
    [InlineData("二连击")]
    public void NormalEchoSharesItsOriginatingAttackButKeepsItsOwnNumber(string attack)
    {
        var before = Room(0);
        before.Slots[0].CharacterElement = ElementType.Fire;
        before.Slots[0].OutgoingElementModifierPercent = 25;
        var after = Room(1, 425);
        AddLogs(after, $"1号位 岚 {attack} 史莱姆，造成 50 点伤害（暴击）。",
            "1号位 岚 对 史莱姆 造成 25 点普攻追击伤害。");
        var plan = BattleFeedbackPlanner.Create(before, after)!;
        Assert.Equal(new[] { false, true }, plan.Events.Select(e => e.IsFollowUp));
        Assert.Equal(new[] { 50, 25 }, plan.Events.Select(e => e.Amount));
        Assert.Equal("普攻追击", plan.Events[1].Label);
        Assert.Equal(2, plan.HitCount);
        Assert.Equal(75, plan.TotalDamage);
        Assert.Equal(1, plan.CriticalCount);
        Assert.Equal("fire", plan.Events[1].Tone);
        Assert.Equal(25, plan.Events[1].ElementModifierPercent);
    }

    [Fact]
    public void DoubleAttackAndIndependentSkillsKeepTheirOwnLaunches()
    {
        var after = Room(1);
        AddLogs(after, "1号位 岚 普通攻击 史莱姆，造成 40 点伤害。",
            "1号位 岚 对 史莱姆 造成 20 点普攻追击伤害。",
            "1号位 岚 二连击 史莱姆，造成 40 点伤害。",
            "1号位 岚 对 史莱姆 造成 20 点普攻追击伤害。",
            "2号位 星 使用 火球术 攻击 史莱姆，造成 30 点伤害。",
            "2号位 星 触发 奥术掌握追加飞弹 攻击 史莱姆，造成 10 点伤害。");
        var plan = BattleFeedbackPlanner.Create(Room(0), after)!;
        Assert.Equal(new[] { false, true, false, true, false, false }, plan.Events.Select(e => e.IsFollowUp));
        Assert.Equal(6, plan.HitCount);
        Assert.Equal(160, plan.TotalDamage);
    }

    [Theory]
    [InlineData("2号位 星 普通攻击 史莱姆，造成 40 点伤害。")]
    [InlineData("1号位 岚 使用 斩击 攻击 史莱姆，造成 40 点伤害。")]
    [InlineData("史莱姆 受到 中毒 造成的 8 点伤害。")]
    [InlineData("未识别的战报。")]
    public void EchoWithoutItsOwnAdjacentNormalAttackIsNotMerged(string precedingLog)
    {
        var after = Room(1);
        AddLogs(after, "1号位 岚 普通攻击 史莱姆，造成 40 点伤害。", precedingLog,
            "1号位 岚 对 史莱姆 造成 20 点普攻追击伤害。");
        Assert.False(BattleFeedbackPlanner.Create(Room(0), after)!.Events.Last().IsFollowUp);
    }

    [Fact]
    public void KillingEchoStaysWithItsAttackBeforeTheNextMonsterAppears()
    {
        var before = Room(0, 60);
        var after = Room(1);
        after.CurrentEnemyNumber++;
        AddLogs(after, "1号位 岚 普通攻击 史莱姆，造成 40 点伤害。",
            "1号位 岚 对 史莱姆 造成 20 点普攻追击伤害。", "史莱姆 已被击败。");
        var plan = BattleFeedbackPlanner.Create(before, after)!;
        Assert.True(plan.Defeated);
        Assert.True(plan.Events[1].IsFollowUp);
        Assert.Equal(0, plan.FinalVitals["enemy"].Hp);
        Assert.Equal(2, plan.HitCount);
    }

    [Fact]
    public void ActualStatusesAndUtilityEffectsHaveCalloutsWithoutAddingDamageHits()
    {
        var before = Room(0);
        var after = Room(1, 480);
        after.Slots[0].StatusEffects.Add(new() { Name = "攻击提升", IsPositive = true });
        after.MonsterEffects.Add(new() { Name = "破甲", IsPositive = false });
        AddLogs(after, "1号位 岚 获得 攻击提升，持续 3 回合。",
            "史莱姆 获得 破甲，持续 2 回合。",
            "1号位 岚 使用 净化，移除了 2号位 星 的 中毒。",
            "2号位 星 使用 驱散，驱散了 史莱姆 的 防御提升。",
            "2号位 星 的 稳定引导 使 1 个伤害技能的冷却缩短 1 回合。",
            "魂印「冰心」打断了 史莱姆 的行动。",
            "1号位 岚 普通攻击 史莱姆，造成 20 点伤害。");
        var plan = BattleFeedbackPlanner.Create(before, after)!;
        Assert.Equal(new[] { "buff", "debuff", "cleanse", "dispel", "cooldown", "interrupt", "damage" }, plan.Events.Select(e => e.Kind));
        Assert.Equal(new[] { "1", "enemy", "2", "enemy", "2", "enemy", "enemy" }, plan.Events.Select(e => e.Target));
        Assert.Equal(new[] { "攻击提升", "破甲", "净化", "强化驱散", "冷却缩短 1 回合", "打断" }, plan.Events.Take(6).Select(e => e.Label));
        Assert.Equal(20, plan.TotalDamage);
        Assert.Equal(1, plan.HitCount);
    }

    [Fact]
    public void OperationPotionShowsItsActualBoostsAndUnknownStatusStaysNeutral()
    {
        var after = Room(1);
        AddLogs(after, "1号位 岚 使用 猛攻药剂，获得 攻击 +20% · 普通攻击伤害 +15% · 每场 1 瓶。",
            "史莱姆 获得 未知状态，持续 2 回合。");
        var plan = BattleFeedbackPlanner.Create(Room(0), after)!;
        Assert.Equal("攻击 +20% · 普通攻击伤害 +15%", plan.Events[0].Label);
        Assert.Equal("buff", plan.Events[0].Kind);
        Assert.Equal("status", plan.Events[1].Kind);
        Assert.Equal("neutral", plan.Events[1].Tone);
        Assert.Equal(0, plan.HitCount);
        Assert.Equal(0, plan.TotalDamage);
    }

    private static void AddLogs(RoomDetailResponse room, params string[] texts)
    {
        for (var i = 0; i < texts.Length; i++) room.BattleLogs.Add(new() { Id = 101 + i, Text = texts[i] });
    }

    private static RoomDetailResponse Room(int round, int monsterHp = 500) => new()
    {
        RoomId = 1, RunSequence = 1, RoundNumber = round, MonsterName = "史莱姆",
        MonsterHp = monsterHp, MonsterMaxHp = 500, CurrentWaveNumber = 1, CurrentEnemyNumber = 1,
        Slots = [
            new() { SlotIndex = 1, CharacterId = 1, CharacterName = "岚", ProfessionName = "剑士", CharacterHp = 100, CharacterMaxHp = 100, IsOccupied = true, IsAlive = true },
            new() { SlotIndex = 2, CharacterId = 2, CharacterName = "星", ProfessionName = "法师", CharacterHp = 100, CharacterMaxHp = 100, IsOccupied = true, IsAlive = true }
        ]
    };
}
