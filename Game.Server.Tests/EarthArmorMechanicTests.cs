using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Theory]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    public async Task EarthArmorHpBoundaryGivesCompleteWindowAndOnlyActiveBreakGrantsThreeRewardRounds(int window, bool broken)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var options = EarthTestOptions();
        options.Profiles["fire-test"].EarthArmor!.WindowRounds = window;
        var rig = await FireRigAsync(test, options);
        test.Room.RoundNumber = 60;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        test.Room.RoundNumber = 61;
        test.Monster.Hp = 7001;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal(0, state.ActivationCount); // Crossing mid-round cannot shorten the handling window.
        var lastWindowRound = 62 + window - 1;
        for (var round = 62; round <= lastWindowRound; round++)
        {
            test.Room.RoundNumber = round;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
            Assert.True(state.IsActive);
            Assert.Equal(30m, await rig.Statuses.ModifierAsync(test.Room, "Monster", test.Monster.Id, "ReductionPercent"));
            if (broken)
                await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Wind,
                    round == lastWindowRound ? 250 - 50 * (window - 1) : 50);
            await rig.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, []);
            await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
        }
        Assert.False(state.IsActive);
        Assert.Equal(1, state.ActivationCount);
        Assert.Equal(broken ? 1 : 0, state.BreakCount);
        Assert.Equal(broken ? 0 : 1, state.ExpiryCount);
        Assert.Equal(0m, await rig.Statuses.ModifierAsync(test.Room, "Monster", test.Monster.Id, "ReductionPercent"));
        for (var round = lastWindowRound + 1; round <= lastWindowRound + 4; round++)
        {
            test.Room.RoundNumber = round;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
            Assert.Equal(broken && round <= lastWindowRound + 3 ? 120 : 100,
                await rig.Statuses.AmplifyDamageAsync(test.Room, test.Monster.Id, 100));
            Assert.Equal(0m, await rig.Statuses.ModifierAsync(test.Room, "Monster", test.Monster.Id, "AttackPercent"));
            await rig.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, []);
        }
        test.Monster.Hp = 2000;
        test.Room.RoundNumber = 100;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        Assert.Equal(1, state.ActivationCount);
    }

    [Fact]
    public async Task EarthArmorCannotBeDispelledButWindNormalEchoCanFinishBreakingIt()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 200);
        var rig = await FireRigAsync(test, EarthTestOptions());
        test.Monster.Element = ElementType.Earth;
        test.Monster.Hp = 7000;
        test.Character.WeaponNormalEchoPercent = 100;
        test.Db.CharacterWeapons.Add(new()
        {
            CharacterId = test.Character.Id, EquippedSlotIndex = 1, WeaponCode = "test-wind", Name = "Wind",
            Element = ElementType.Wind, MaxHp = 1
        });
        await test.Db.SaveChangesAsync();
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        Assert.Null(await rig.Statuses.RemoveFirstAsync(test.Room, "Monster", [test.Monster.Id], true));
        Assert.Equal(30m, await rig.Statuses.ModifierAsync(test.Room, "Monster", test.Monster.Id, "ReductionPercent"));
        var executor = new BattleRoundExecutor(test.Db, ConsumableTestFactory.Create(), rig.Skills,
            rig.Statuses, rig.Effects, rig.Monster);
        using var recording = rig.Statuses.Events.Begin(test.Room, test.Monster, rig.Party);
        using var settlement = await rig.Statuses.BeginSettlementAsync(test.Room);
        await executor.ExecuteAsync(test.Room, test.Monster, rig.Party, [], []);
        var damage = rig.Statuses.Events.Snapshot(test.Room)
            .Where(e => e.Kind == BattleEventKind.Damage && e.Target.ActorType == "Monster").ToList();
        Assert.Contains(damage, e => e.SkillCode == "normal-echo");
        Assert.True(damage[0].ActualAmount < 250);
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal(1, state.BreakCount);
        Assert.Equal(damage.Where(e => e.Element == ElementType.Wind).Sum(e => (long)e.ActualAmount), state.ElementDamage);
    }

    [Fact]
    public async Task EarthArmorCountsPostReductionWindDamageAndClipsOverkillWhileOtherElementsAndDotDoNotProgress()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        var rig = await FireRigAsync(test, EarthTestOptions());
        test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        var source = BattleActor.ForCharacter(rig.Party[0]);
        await rig.Damage.CharacterDamageAsync(rig.Context, source, BattleSkillEffect.Damage(100), BattleDamageOrigin.Skill,
            false, damageElement: ElementType.Water);
        Assert.Equal(0, state.ElementDamage);
        await rig.Statuses.ApplyAsync(test.Room, "Monster", test.Monster.Id, "fire-test-dot", 3, [], "Boss", perTickValue: 800);
        test.Room.RoundNumber++;
        await rig.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, []);
        Assert.Equal(0, state.ElementDamage);
        test.Monster.Element = ElementType.Earth;
        var damage = await rig.Damage.CharacterDamageAsync(rig.Context, source, BattleSkillEffect.Damage(100),
            BattleDamageOrigin.Skill, false, damageElement: ElementType.Wind);
        Assert.Equal(87, damage.ActualAmount); // 100 flat * 125% Wind advantage * 70% after armor.
        Assert.Equal(damage.ActualAmount, state.ElementDamage);
        test.Monster.Hp = 7;
        var overkill = await rig.Damage.CharacterDamageAsync(rig.Context, source, BattleSkillEffect.Damage(1000),
            BattleDamageOrigin.Counter, false, damageElement: ElementType.Wind);
        Assert.Equal(7, overkill.ActualAmount);
        Assert.Equal(94, state.ElementDamage);
        Assert.Equal(0, state.BreakCount);
    }

    [Fact]
    public async Task EarthArmorProgressSurvivesReloadAndBreakingRemovesArmorBeforeTheNextDamageSegment()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test, EarthTestOptions());
        test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Wind, 100);
        await test.Db.SaveChangesAsync();
        await using var db = test.CreateDbContext();
        var room = await db.Rooms.SingleAsync();
        var monster = await db.Monsters.SingleAsync();
        room.RoundNumber++;
        var statuses = new BattleStatusService(db, rig.Catalog.Statuses);
        var phases = new MonsterPhaseService(db, rig.Catalog, statuses);
        await phases.BeginRoundAsync(room, monster, []);
        var context = new BattleExecutionContext(room, monster, [], new Dictionary<int, ElementType>(),
            new Dictionary<int, OperationPotionBonuses>(), []);
        await phases.ObserveDirectDamageAsync(context, ElementType.Wind, 149);
        Assert.True((await db.BattleMonsterPhaseStates.SingleAsync()).IsActive);
        await phases.ObserveDirectDamageAsync(context, ElementType.Wind, 1);
        Assert.Equal(0m, await statuses.ModifierAsync(room, "Monster", monster.Id, "ReductionPercent"));
        Assert.Equal(100, await statuses.AmplifyDamageAsync(room, monster.Id, 100)); // No partial reward round.
        await phases.ObserveDirectDamageAsync(context, ElementType.Wind, 1000);
        var state = await db.BattleMonsterPhaseStates.SingleAsync();
        Assert.Equal(250, state.ElementDamage);
        Assert.Equal(1, state.BreakCount);
        await db.SaveChangesAsync();
        room.RoundNumber++;
        await phases.BeginRoundAsync(room, monster, []);
        Assert.Equal(120, await statuses.AmplifyDamageAsync(room, monster.Id, 100));
    }

    [Fact]
    public async Task EarthArmorKeepsAnExistingFrozenRoomWithoutTheNewMechanic()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test, EarthTestOptions());
        var oldOptions = EarthTestOptions();
        oldOptions.Profiles["fire-test"].EarthArmor = null;
        var depths = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions()));
        var rewards = RewardTestFactory.CreateCatalog();
        var oldRules = new DungeonRunRulesService(test.Db, new MonsterCombatCatalog(Options.Create(oldOptions)),
            rewards, PartyScalingCatalog.Default, depths);
        var oldDefinition = await oldRules.EnsureAsync(test.Room);
        await test.Db.SaveChangesAsync();
        var rules = new DungeonRunRulesService(test.Db, rig.Catalog, rewards, PartyScalingCatalog.Default, depths);
        var phases = new MonsterPhaseService(test.Db, rig.Catalog,
            new BattleStatusService(test.Db, rig.Catalog.Statuses, runRules: rules), rules);
        test.Monster.Hp = 7000;
        await phases.BeginRoundAsync(test.Room, test.Monster, []);
        Assert.Empty(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Null(phases.EarthArmorDefinition(test.Room, test.Monster));
        Assert.Equal(oldDefinition.Revision, (await rules.EnsureAsync(test.Room)).Revision);
    }

    [Fact]
    public void EarthArmorDeclarationsRejectInvalidTriggersAndMixedMechanicsAndSurviveExportAndDepthReplacement()
    {
        foreach (var fault in new Action<MonsterCombatOptions>[]
        {
            o => o.Profiles["fire-test"].EarthArmor!.TriggerHpPercent = 100,
            o => o.Profiles["fire-test"].EarthArmor!.WindowRounds = 0,
            o => o.Profiles["fire-test"].EarthArmor!.BreakWindDamagePercent = 0,
            o => o.StatusEffects.Single(s => s.Code == "earth-test-armor").IsDispellable = true,
            o => o.Profiles["fire-test"].FireCore = FireTestOptions().Profiles["fire-test"].FireCore
        })
        {
            var options = EarthTestOptions();
            fault(options);
            Assert.Throws<InvalidOperationException>(() => new MonsterCombatCatalog(Options.Create(options)));
        }
        var good = EarthTestOptions();
        good.Profiles["base"] = new() { DepthProgressionCode = "earth" };
        good.DepthProgressions["earth"] = [new() { Depth = 2, ReplacementProfileCode = "fire-test" }];
        var original = new MonsterCombatCatalog(Options.Create(good));
        var rebuilt = new MonsterCombatCatalog(Options.Create(original.ExportOptions()));
        var armor = rebuilt.FindProfile(rebuilt.ResolveDepthProfile("base", 2))!.EarthArmor!;
        Assert.Equal(2.5m, armor.BreakWindDamagePercent);
        Assert.Contains("矿脉护甲", rebuilt.GetAddedMechanics("base", 2));
    }

    private static MonsterCombatOptions EarthTestOptions()
    {
        var options = FireTestOptions();
        options.StatusEffects.Add(new() { Code = "earth-test-armor", Name = "矿脉护甲", Description = "Armor",
            EffectType = "ReductionPercent", ValuePerStack = 30, IsPositive = true, IsDispellable = false });
        options.Profiles["fire-test"].FireCore = null;
        options.Profiles["fire-test"].EarthArmor = new()
        {
            TriggerHpPercent = 70, WindowRounds = 4, BreakWindDamagePercent = 2.5m,
            ArmorStatusCode = "earth-test-armor", RewardStatusCode = "fire-test-reward", RewardRounds = 3
        };
        return options;
    }
}
