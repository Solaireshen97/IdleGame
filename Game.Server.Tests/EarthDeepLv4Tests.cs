using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    [InlineData(0, true)]
    public async Task EarthLv4FixedCyclesResetWindProgressKeepOldResonanceAndGrantFullRewardsAcrossReloads(
        int breakPhaseRound, bool firstTimeout)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test, EarthCycleTestOptions());
        test.Room.RoundNumber = 60;
        await test.Db.SaveChangesAsync();
        var stacks = 0;
        int? lastBreak = null;
        var breaks = 0;
        var expiries = 0;
        for (var local = 1; local <= 37; local++)
        {
            await using var db = test.CreateDbContext();
            var room = await db.Rooms.SingleAsync();
            var monster = await db.Monsters.SingleAsync();
            room.RoundNumber = 60 + local - 1;
            var statuses = new BattleStatusService(db, rig.Catalog.Statuses);
            var phases = new MonsterPhaseService(db, rig.Catalog, statuses);
            // An intent preview can establish the encounter clock before phase preparation.
            Assert.Equal(local - 1, await phases.EncounterSkillRoundAsync(room, monster));
            await phases.BeginRoundAsync(room, monster, []);
            await phases.BeginRoundAsync(room, monster, []);
            var state = db.BattleMonsterPhaseStates.Local.Single();
            var phaseRound = local < 6 ? 0 : (local - 6) % 8 + 1;
            var cycle = local < 6 ? 0 : (local - 6) / 8 + 1;
            var canBreak = breakPhaseRound > 0 && !(firstTimeout && cycle == 1);
            Assert.Equal(local < 6 ? 0 : cycle, state.ActivationCount);
            Assert.Equal(local < 6 ? 6 : 6 + cycle * 8, state.NextActivationRound);
            Assert.Equal(stacks * 10m, await statuses.ModifierAsync(room, "Monster", monster.Id, "AttackPercent"));
            Assert.Equal(lastBreak is { } previous && local > previous && local <= previous + 3 ? 120 : 100,
                await statuses.AmplifyDamageAsync(room, monster.Id, 100));
            if (phaseRound == 1) Assert.Equal(0, state.ElementDamage);
            if (phaseRound is >= 1 and <= 5 && (!canBreak || phaseRound <= breakPhaseRound))
            {
                Assert.True(state.IsActive);
                var context = new BattleExecutionContext(room, monster, [], new Dictionary<int, ElementType>(),
                    new Dictionary<int, OperationPotionBonuses>(), []);
                var amount = !canBreak ? 10 : phaseRound == breakPhaseRound ? 600 - 100 * (phaseRound - 1) : 100;
                await phases.ObserveDirectDamageAsync(context, ElementType.Wind, amount);
                if (canBreak && phaseRound == breakPhaseRound)
                {
                    lastBreak = local;
                    breaks++;
                    stacks = 0;
                    Assert.False(state.IsActive);
                    Assert.Equal(600, state.ElementDamage);
                    Assert.Equal(0m, await statuses.ModifierAsync(room, "Monster", monster.Id, "ReductionPercent"));
                    Assert.Equal(0m, await statuses.ModifierAsync(room, "Monster", monster.Id, "AttackPercent"));
                    Assert.Equal(100, await statuses.AmplifyDamageAsync(room, monster.Id, 100));
                    await phases.ObserveDirectDamageAsync(context, ElementType.Wind, 1000);
                    Assert.Equal(600, state.ElementDamage);
                }
            }
            await statuses.ResolveEndOfRoundAsync(room, monster, [], []);
            await phases.EndRoundAsync(room, monster, []);
            await phases.EndRoundAsync(room, monster, []);
            if (phaseRound is >= 1 and < 5 && (!canBreak || phaseRound < breakPhaseRound))
                stacks = Math.Max(stacks, Math.Min(3, phaseRound));
            if (phaseRound == 5 && !canBreak) expiries++;
            Assert.Equal(stacks, await statuses.StacksAsync(room, "Monster", monster.Id, "earth-test-resonance"));
            Assert.Equal(breaks, state.BreakCount);
            Assert.Equal(expiries, state.ExpiryCount);
            Assert.Equal(phaseRound is >= 1 and < 5 && (!canBreak || phaseRound < breakPhaseRound), state.IsActive);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public void EarthLv4RejectsMixedOrOverlappingSchedulesAndExportsTheCycle()
    {
        foreach (var fault in new Action<EarthArmorOptions>[]
        {
            a => a.TriggerHpPercent = 70, a => a.FirstActivationRound = 0,
            a => a.CycleRounds = 0, a => a.CycleRounds = 7, a => a.FirstActivationRound = 251
        })
        {
            var options = EarthCycleTestOptions();
            fault(options.Profiles["fire-test"].EarthArmor!);
            Assert.Throws<InvalidOperationException>(() => new MonsterCombatCatalog(Options.Create(options)));
        }
        var old = System.Text.Json.JsonSerializer.Deserialize<EarthArmorOptions>(
            "{\"TriggerHpPercent\":70,\"WindowRounds\":5,\"BreakWindDamagePercent\":4}")!;
        Assert.Equal(70, old.TriggerHpPercent);
        Assert.Equal(0, old.FirstActivationRound);
        Assert.Equal(0, old.CycleRounds);
        var catalog = new MonsterCombatCatalog(Options.Create(EarthCycleTestOptions()));
        var restored = new MonsterCombatCatalog(Options.Create(catalog.ExportOptions()));
        var armor = restored.FindProfile("fire-test")!.EarthArmor!;
        Assert.Null(armor.TriggerHpPercent);
        Assert.Equal((6, 8, 5, 3), (armor.FirstActivationRound, armor.CycleRounds, armor.WindowRounds, armor.RewardRounds));
        Assert.Equal(new[] { "矿脉护甲", "地脉共鸣", "深岩循环" }, restored.GetAddedMechanics("fire-test", 1));
    }

    [Fact]
    public async Task EarthLv4KeepsAnExistingFrozenHpTriggeredRoomOneShot()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var oldRig = await FireRigAsync(test, EarthResonanceTestOptions());
        var depths = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions()));
        var rewards = RewardTestFactory.CreateCatalog();
        var oldRules = new DungeonRunRulesService(test.Db, oldRig.Catalog, rewards, PartyScalingCatalog.Default, depths);
        var frozen = await oldRules.EnsureAsync(test.Room);
        await test.Db.SaveChangesAsync();
        var newCatalog = new MonsterCombatCatalog(Options.Create(EarthCycleTestOptions()));
        var rules = new DungeonRunRulesService(test.Db, newCatalog, rewards, PartyScalingCatalog.Default, depths);
        var statuses = new BattleStatusService(test.Db, newCatalog.Statuses, runRules: rules);
        var phases = new MonsterPhaseService(test.Db, newCatalog, statuses, rules);
        test.Monster.Hp = 7000;
        for (var local = 1; local <= 30; local++)
        {
            test.Room.RoundNumber = local - 1;
            await phases.BeginRoundAsync(test.Room, test.Monster, []);
            await statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, [], []);
            await phases.EndRoundAsync(test.Room, test.Monster, []);
        }
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal(1, state.ActivationCount);
        Assert.Equal(1, state.ExpiryCount);
        Assert.Equal(70, phases.EarthArmorDefinition(test.Room, test.Monster)!.TriggerHpPercent);
        Assert.Equal(0, phases.EarthArmorDefinition(test.Room, test.Monster)!.CycleRounds);
        Assert.Equal(frozen.Revision, (await rules.EnsureAsync(test.Room)).Revision);
    }

    [Fact]
    public async Task EarthLv4BossDeathCancelsTheFinalWindowAndRunResetClearsItsClock()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test, EarthCycleTestOptions());
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        test.Room.RoundNumber = 5;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        test.Monster.Hp = 0;
        await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.False(state.IsActive);
        Assert.Equal(0, state.ExpiryCount);
        Assert.Equal(0, state.BreakCount);
        Assert.Equal(0, await rig.Statuses.StacksAsync(test.Room, "Monster", test.Monster.Id, "earth-test-resonance"));
        await test.Db.SaveChangesAsync();
        await rig.Phases.ResetRoomAsync(test.Room.Id);
        await test.Db.SaveChangesAsync();
        Assert.Empty(await test.Db.BattleMonsterPhaseStates.ToListAsync());
    }

    private static MonsterCombatOptions EarthCycleTestOptions()
    {
        var options = EarthResonanceTestOptions();
        var armor = options.Profiles["fire-test"].EarthArmor!;
        armor.TriggerHpPercent = null;
        armor.FirstActivationRound = 6;
        armor.CycleRounds = 8;
        armor.BreakWindDamagePercent = 6;
        return options;
    }
}
