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
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindStaticHpBoundaryGivesFourFullRoundsAndOnlyActiveClearRewards(bool cleared)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test, WindFieldTestOptions());
        test.Room.RoundNumber = 60;
        test.Monster.Hp = 7001;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal(0, state.ActivationCount);
        for (var round = 61; round <= 64; round++)
        {
            test.Room.RoundNumber = round;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
            Assert.True(state.IsActive);
            Assert.Equal(64, state.ExpiresAfterRound);
            if (cleared)
            {
                for (var segment = 0; segment < 3; segment++)
                    await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Fire, 1, test.Character.Id);
                Assert.Equal(round - 60, state.LinkedHitCount);
            }
            await rig.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, []);
            await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
            await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
            Assert.Equal(cleared ? (round < 63 ? 2 : round == 63 ? 1 : 0) : (round == 61 ? 3 : round < 64 ? 4 : 0),
                await rig.Statuses.StacksAsync(test.Room, "Monster", test.Monster.Id, "wind-test-static"));
        }
        Assert.False(state.IsActive);
        Assert.Equal(1, state.ActivationCount);
        Assert.Equal(cleared ? 1 : 0, state.BreakCount);
        Assert.Equal(cleared ? 0 : 1, state.ExpiryCount);
        for (var round = 65; round <= 68; round++)
        {
            test.Room.RoundNumber = round;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
            Assert.Equal(cleared && round <= 67 ? 120 : 100,
                await rig.Statuses.AmplifyDamageAsync(test.Room, test.Monster.Id, 100));
            Assert.Equal(0m, await rig.Statuses.ModifierAsync(test.Room, "Monster", test.Monster.Id, "AttackPercent"));
            await rig.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, []);
        }
        test.Room.RoundNumber = 100;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        Assert.Equal(1, state.ActivationCount);
    }

    [Fact]
    public async Task WindStaticDeduplicatesCharactersButTwoFireActorsCanClearInTheFirstRound()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var other = await test.AddSlotAsync(2, "Second Fire");
        var rig = await FireRigAsync(test, WindFieldTestOptions());
        test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        Assert.Null(await rig.Statuses.RemoveFirstAsync(test.Room, "Monster", [test.Monster.Id], true));
        await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Water, 100, test.Character.Id);
        await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Fire, 0, test.Character.Id);
        await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Fire, 100); // Derived echo has no actor count.
        await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Fire, 100, 99999);
        Assert.Equal(2, await rig.Statuses.StacksAsync(test.Room, "Monster", test.Monster.Id, "wind-test-static"));
        await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Fire, 100, test.Character.Id);
        await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Fire, 100, test.Character.Id);
        Assert.Equal(1, await rig.Statuses.StacksAsync(test.Room, "Monster", test.Monster.Id, "wind-test-static"));
        await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Fire, 100, other.Id);
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal(2, state.LinkedHitCount);
        Assert.Equal(1, state.BreakCount);
        Assert.False(state.IsActive);
        Assert.Equal(100, await rig.Statuses.AmplifyDamageAsync(test.Room, test.Monster.Id, 100));
        await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
        test.Room.RoundNumber++;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        Assert.Equal(120, await rig.Statuses.AmplifyDamageAsync(test.Room, test.Monster.Id, 100));
    }

    [Fact]
    public async Task WindStaticHitAndGrowthProgressSurviveReloadWithoutDoubleCounting()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var options = WindFieldTestOptions();
        var rig = await FireRigAsync(test, options);
        test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Fire, 1, test.Character.Id);
        await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
        await test.Db.SaveChangesAsync();
        await using var db = test.CreateDbContext();
        var room = await db.Rooms.SingleAsync();
        var monster = await db.Monsters.SingleAsync();
        var character = await db.Characters.SingleAsync();
        var slot = await db.RoomSlots.SingleAsync();
        var catalog = new MonsterCombatCatalog(Options.Create(options));
        var statuses = new BattleStatusService(db, catalog.Statuses);
        var phases = new MonsterPhaseService(db, catalog, statuses);
        var context = new BattleExecutionContext(room, monster, [new(slot, character)],
            new Dictionary<int, ElementType>(), new Dictionary<int, OperationPotionBonuses>(), []);
        await phases.BeginRoundAsync(room, monster, []);
        await phases.ObserveDirectDamageAsync(context, ElementType.Fire, 1, character.Id);
        await phases.EndRoundAsync(room, monster, []);
        Assert.Equal(2, await statuses.StacksAsync(room, "Monster", monster.Id, "wind-test-static"));
        Assert.Equal(1, (await db.BattleMonsterPhaseStates.SingleAsync()).LinkedHitCount);
        room.RoundNumber++;
        await phases.BeginRoundAsync(room, monster, []);
        await phases.ObserveDirectDamageAsync(context, ElementType.Fire, 1, character.Id);
        await phases.EndRoundAsync(room, monster, []);
        Assert.Equal(2, await statuses.StacksAsync(room, "Monster", monster.Id, "wind-test-static"));
        Assert.Equal(2, (await db.BattleMonsterPhaseStates.SingleAsync()).LinkedHitCount);
    }

    [Fact]
    public async Task WindStaticAmplifiesOnlyLinkedStormAndDotDoesNotRemoveStacks()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 1000);
        var rig = await FireRigAsync(test, WindFieldTestOptions());
        test.Character.MaxHp = 1000;
        test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        Assert.Equal(10m, await rig.Phases.StaticFieldSkillBonusAsync(test.Room, test.Monster, "fire-test-slash"));
        Assert.Equal(0m, await rig.Phases.StaticFieldSkillBonusAsync(test.Room, test.Monster, "basic-attack"));
        var target = BattleActor.ForCharacter(rig.Party[0]);
        var hit = await rig.Damage.MonsterDamageAsync(rig.Context, target,
            new(BattleEffectKind.Damage, new(BattleTargetSide.Opponent, BattleTargetSelection.AllAlive, false, false, "AllAlive")),
            true, skillBonusPercent: 10);
        Assert.Equal(110, hit.ActualAmount);
        await rig.Statuses.ApplyAsync(test.Room, "Monster", test.Monster.Id, "fire-test-dot", 3, [], "Boss", perTickValue: 10);
        test.Room.RoundNumber++;
        await rig.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, []);
        Assert.Equal(2, await rig.Statuses.StacksAsync(test.Room, "Monster", test.Monster.Id, "wind-test-static"));
        Assert.Equal(0, Assert.Single(test.Db.BattleMonsterPhaseStates.Local).LinkedHitCount);
    }

    [Fact]
    public async Task WindStaticDeathCancelsWithoutExpiryOrRewardAndRoomResetRemovesPhase()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test, WindFieldTestOptions());
        test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        test.Monster.Hp = 0;
        await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Fire, 1, test.Character.Id);
        await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.False(state.IsActive);
        Assert.Equal(0, state.ExpiryCount);
        Assert.Equal(0, state.BreakCount);
        Assert.Null(state.RewardStartsAtRound);
        Assert.Equal(0, await rig.Statuses.StacksAsync(test.Room, "Monster", test.Monster.Id, "wind-test-static"));
        await test.Db.SaveChangesAsync();
        await rig.Monster.ResetRoomStateAsync(test.Room.Id);
        await test.Db.SaveChangesAsync();
        Assert.Empty(await test.Db.BattleMonsterPhaseStates.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindStaticFrozenRoomKeepsOriginalDeclarationsWhenLiveConfigChanges(bool hadField)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var options = WindFieldTestOptions();
        if (!hadField) options.Profiles["fire-test"].StaticField = null;
        var rig = await FireRigAsync(test, options);
        test.Monster.Hp = 7000;
        var depths = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions()));
        var rewards = RewardTestFactory.CreateCatalog();
        var frozenRules = new DungeonRunRulesService(test.Db, rig.Catalog, rewards, PartyScalingCatalog.Default, depths);
        var original = await frozenRules.EnsureAsync(test.Room);
        await test.Db.SaveChangesAsync();
        options = WindFieldTestOptions();
        options.Profiles["fire-test"].StaticField!.RemovalElement = ElementType.Water;
        options.Profiles["fire-test"].StaticField!.GrowthStacksPerRound = 2;
        var liveCatalog = new MonsterCombatCatalog(Options.Create(options));
        var liveRules = new DungeonRunRulesService(test.Db, liveCatalog, rewards, PartyScalingCatalog.Default, depths);
        var statuses = new BattleStatusService(test.Db, liveCatalog.Statuses, runRules: liveRules);
        var phases = new MonsterPhaseService(test.Db, liveCatalog, statuses, liveRules);
        await phases.BeginRoundAsync(test.Room, test.Monster, []);
        if (hadField)
        {
            Assert.Equal(ElementType.Fire, phases.StaticFieldDefinition(test.Room, test.Monster)!.RemovalElement);
            await phases.ObserveDirectDamageAsync(rig.Context, ElementType.Fire, 1, test.Character.Id);
            await phases.EndRoundAsync(test.Room, test.Monster, []);
            Assert.Equal(2, await statuses.StacksAsync(test.Room, "Monster", test.Monster.Id, "wind-test-static"));
        }
        else
        {
            Assert.Null(phases.StaticFieldDefinition(test.Room, test.Monster));
            Assert.Empty(test.Db.BattleMonsterPhaseStates.Local);
        }
        Assert.Equal(original.Revision, (await liveRules.EnsureAsync(test.Room)).Revision);
    }

    [Fact]
    public void WindStaticCatalogRejectsOverlappingPhasesAndPreservesExportedField()
    {
        var options = WindFieldTestOptions();
        var catalog = new MonsterCombatCatalog(Options.Create(options));
        var exported = catalog.ExportOptions();
        Assert.Equal(2, new MonsterCombatCatalog(Options.Create(exported)).FindProfile("fire-test")!.StaticField!.InitialStacks);
        exported.Profiles["fire-test"].StaticField!.InitialStacks = 3;
        Assert.Equal(2, catalog.FindProfile("fire-test")!.StaticField!.InitialStacks);
        options.Profiles["fire-test"].EarthArmor = new();
        Assert.Throws<InvalidOperationException>(() => new MonsterCombatCatalog(Options.Create(options)));
        options = WindFieldTestOptions();
        options.Profiles["fire-test"].StaticField!.GrowthRounds = 4;
        Assert.Throws<InvalidOperationException>(() => new MonsterCombatCatalog(Options.Create(options)));
    }

    private static MonsterCombatOptions WindFieldTestOptions()
    {
        var options = FireTestOptions();
        options.Profiles["fire-test"].FireCore = null;
        options.Skills[0].TargetType = "AllAlive";
        options.StatusEffects.AddRange([
            new() { Code="wind-test-static", Name="Static", Description="Static", EffectType="None", MaxStacks=5,
                InitialStacks=2, CounterKind=BattleStatusCounterKind.Stacks, IsPositive=true, IsDispellable=false },
            new() { Code="wind-test-hit", Name="Hit", Description="Hit", EffectType="None", IsPositive=true,
                IsDispellable=false, IsHidden=true, Lifetime=BattleStatusLifetime.CurrentRound },
            new() { Code="wind-test-growth", Name="Growth", Description="Growth", EffectType="None", IsPositive=true,
                IsDispellable=false, IsHidden=true, Lifetime=BattleStatusLifetime.Encounter, MaxStacks=2,
                CounterKind=BattleStatusCounterKind.Stacks }
        ]);
        options.Profiles["fire-test"].StaticField = new()
        {
            TriggerHpPercent=70, WindowRounds=4, InitialStacks=2, GrowthRounds=2, GrowthStacksPerRound=1,
            StaticStatusCode="wind-test-static", HitUsedStatusCode="wind-test-hit", GrowthUsedStatusCode="wind-test-growth",
            AmplifiedSkillCode="fire-test-slash", SkillDamagePercentPerStack=5, RewardStatusCode="fire-test-reward", RewardRounds=3
        };
        return options;
    }
}
