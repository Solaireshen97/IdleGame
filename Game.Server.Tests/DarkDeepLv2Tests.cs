using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DarkPlagueHpTriggerGivesFourCompleteRoundsAndLastRoundCleanseGetsFullReward(bool cleanse)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 1000);
        var rig = await PlagueRigAsync(test);
        test.Room.RoundNumber = 60; test.Monster.Hp = 7001;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        Assert.Equal(0, await rig.Statuses.StacksAsync(test.Room, "Character", test.Character.Id, "plague-test"));
        for (var round = 61; round <= 64; round++)
        {
            test.Room.RoundNumber = round;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            Assert.Equal(round - 60, await rig.Statuses.StacksAsync(test.Room, "Character", test.Character.Id, "plague-test"));
            if (cleanse && round == 64)
            {
                var removed = await rig.Statuses.RemoveFirstAsync(test.Room, "Character", [test.Character.Id], false);
                Assert.Equal("plague-test", removed!.Code);
                await rig.Damage.ObserveCleanseAsync(rig.Context, test.Character.Id, removed.Code);
                await rig.Damage.ObserveCleanseAsync(rig.Context, test.Character.Id, removed.Code);
            }
            await rig.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, []);
            await rig.Phases.EndRoundAsync(test.Room, test.Monster, [], rig.Party);
            await rig.Phases.EndRoundAsync(test.Room, test.Monster, [], rig.Party);
        }
        Assert.Equal(cleanse ? 940 : 900, test.Character.Hp);
        Assert.Equal(1000, test.Character.MaxHp);
        for (var round = 65; round <= 69; round++)
        {
            test.Room.RoundNumber = round;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            Assert.Equal(cleanse && round <= 67 ? 15m : 0m,
                await rig.Statuses.ModifierAsync(test.Room, "Character", test.Character.Id, "ReductionPercent"));
            Assert.Equal(cleanse && round <= 67,
                await rig.Phases.SuppressBasicPoisonAsync(test.Room, test.Monster, test.Character.Id, "basic-plague-test"));
            await rig.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, []);
            await rig.Phases.EndRoundAsync(test.Room, test.Monster, [], rig.Party);
        }
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal((1, cleanse ? 1 : 0, cleanse ? 0 : 1, false),
            (state.ActivationCount, state.BreakCount, state.ExpiryCount, state.IsActive));
    }

    [Fact]
    public async Task DarkPlaguePartialLightDamageKeepsLayersAndAccumulationSurvivesReload()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 1000);
        var rig = await PlagueRigAsync(test);
        test.Monster.MaxHp = 1000000; test.Monster.Hp = 700000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        await rig.Damage.ObserveDirectDamageAsync(rig.Context, ElementType.Light, 10000);
        await rig.Damage.ObserveDirectDamageAsync(rig.Context, ElementType.Water, 100000);
        await rig.Damage.ObserveDirectDamageAsync(rig.Context, ElementType.Light, 9999);
        Assert.Equal(1, await rig.Statuses.StacksAsync(test.Room, "Character", test.Character.Id, "plague-test"));
        Assert.Equal(19999, Assert.Single(test.Db.BattleMonsterPhaseStates.Local).ElementDamage);
        await test.Db.SaveChangesAsync();
        await using var db = test.CreateDbContext();
        var room = await db.Rooms.SingleAsync(); var monster = await db.Monsters.SingleAsync();
        var party = new List<BattleParticipant> { new(await db.RoomSlots.SingleAsync(), await db.Characters.SingleAsync()) };
        var statuses = new BattleStatusService(db, rig.Catalog.Statuses);
        var phases = new MonsterPhaseService(db, rig.Catalog, statuses);
        var battle = new BattleExecutionContext(room, monster, party, new Dictionary<int, ElementType>(), new Dictionary<int, OperationPotionBonuses>(), []);
        await phases.ObserveDirectDamageAsync(battle, ElementType.Light, 0);
        Assert.True((await db.BattleMonsterPhaseStates.SingleAsync()).IsActive);
        await phases.ObserveDirectDamageAsync(battle, ElementType.Light, 1);
        await phases.ObserveDirectDamageAsync(battle, ElementType.Light, 100000);
        Assert.Equal(0, await statuses.StacksAsync(room, "Character", party[0].Character.Id, "plague-test"));
        var state = await db.BattleMonsterPhaseStates.SingleAsync();
        Assert.Equal((20000L, 1, false), (state.ElementDamage, state.BreakCount, state.IsActive));
    }

    [Fact]
    public async Task DarkPlagueSnapshotTicksAndGrowthAreIdempotentAcrossRoundServiceReloads()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 1000);
        var rig = await PlagueRigAsync(test);
        test.Room.RoundNumber = 60;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        test.Monster.Attack = 400;
        await test.Db.SaveChangesAsync();
        var hp = 1000;
        for (var round = 60; round <= 63; round++)
        {
            await using var db = test.CreateDbContext();
            var room = await db.Rooms.SingleAsync(); var monster = await db.Monsters.SingleAsync();
            var character = await db.Characters.SingleAsync(); room.RoundNumber = round;
            var party = new List<BattleParticipant> { new(await db.RoomSlots.SingleAsync(), character) };
            var statuses = new BattleStatusService(db, rig.Catalog.Statuses);
            var phases = new MonsterPhaseService(db, rig.Catalog, statuses);
            await phases.BeginRoundAsync(room, monster, [], party);
            await statuses.ResolveEndOfRoundAsync(room, monster, party, []);
            Assert.Equal(hp, character.Hp); // Generic DoT and expiry do not settle managed poison.
            await phases.EndRoundAsync(room, monster, [], party);
            await phases.EndRoundAsync(room, monster, [], party);
            hp -= 10 * (round - 59);
            Assert.Equal(hp, character.Hp);
            Assert.Equal(1000, character.MaxHp);
            if (round < 63)
            {
                var poison = Assert.Single(await statuses.GetActiveAsync(room, "Character", [character.Id]));
                Assert.Equal((BattleStatusLifetime.Rounds, 60, 63),
                    (poison.Lifetime, poison.AppliedRound, poison.ExpiresAfterRound));
            }
            await db.SaveChangesAsync();
        }
        await using var final = test.CreateDbContext();
        Assert.Equal(1, (await final.BattleMonsterPhaseStates.SingleAsync()).ExpiryCount);
        Assert.DoesNotContain(await final.BattleStatusEffects.ToListAsync(), s => s.EffectCode == "plague-test");
    }

    [Fact]
    public async Task DarkPlagueEarlyBreakRewardsOnlyItsTargetAndClearsBossBasicPoisonNextRound()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 1000);
        var other = await test.AddSlotAsync(2, "Healer", hp: 1000);
        var rig = await PlagueRigAsync(test);
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        Assert.True(await rig.Phases.SuppressBasicPoisonAsync(test.Room, test.Monster, test.Character.Id, "basic-plague-test"));
        Assert.False(await rig.Phases.SuppressBasicPoisonAsync(test.Room, test.Monster, other.Id, "basic-plague-test"));
        await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Light, 200);
        Assert.False(await rig.Phases.SuppressBasicPoisonAsync(test.Room, test.Monster, test.Character.Id, "basic-plague-test"));
        await rig.Statuses.ApplyAsync(test.Room, "Character", test.Character.Id, "basic-plague-test", 2, [], "", perTickValue: 20,
            source: new("Monster", test.Monster.Id, "basic-poison"));
        for (var round = 1; round <= 4; round++)
        {
            test.Room.RoundNumber = round;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            Assert.Equal(round <= 3, await rig.Phases.SuppressBasicPoisonAsync(test.Room, test.Monster, test.Character.Id, "basic-plague-test"));
            Assert.False(await rig.Phases.SuppressBasicPoisonAsync(test.Room, test.Monster, other.Id, "basic-plague-test"));
            Assert.False(await rig.Statuses.HasAsync(test.Room, "Character", test.Character.Id, "basic-plague-test"));
            await rig.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, []);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DarkPlagueBossDeathClearsPoisonAndPendingRewardWithoutSuccess(bool alreadyBroken)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 1000);
        var rig = await PlagueRigAsync(test);
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        if (alreadyBroken) await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Light, 200);
        test.Monster.Hp = 0;
        await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Light, 200);
        await rig.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, [], healingOnly: true);
        await rig.Phases.EndRoundAsync(test.Room, test.Monster, [], rig.Party);
        test.Room.RoundNumber++;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        Assert.Equal(1000, test.Character.Hp);
        Assert.Empty(await rig.Statuses.GetActiveAsync(test.Room, "Character", [test.Character.Id]));
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.False(state.IsActive); Assert.Null(state.RewardStartsAtRound);
        Assert.Equal(alreadyBroken ? 1 : 0, state.BreakCount);
    }

    [Fact]
    public async Task DarkPlagueTargetDeathDoesNotRetargetOrRewardAnotherCharacter()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 1000);
        var other = await test.AddSlotAsync(2, "Healer", hp: 1000);
        var rig = await PlagueRigAsync(test);
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        test.Character.Hp = 0;
        await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Light, 200);
        await rig.Phases.EndRoundAsync(test.Room, test.Monster, [], rig.Party);
        test.Room.RoundNumber++;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        Assert.Empty(await rig.Statuses.GetActiveAsync(test.Room, "Character", [other.Id]));
        Assert.Equal(1000, other.Hp);
        Assert.Equal((1, 0, false), (test.Db.BattleMonsterPhaseStates.Local.Single().ActivationCount,
            test.Db.BattleMonsterPhaseStates.Local.Single().BreakCount, test.Db.BattleMonsterPhaseStates.Local.Single().IsActive));
    }

    [Fact]
    public async Task DarkPlagueCleansePrioritizesGrowingPoisonOverOlderDebuffs()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 1000);
        var rig = await PlagueRigAsync(test);
        await rig.Statuses.ApplyAsync(test.Room, "Character", test.Character.Id, "basic-plague-test", 2, [], "", perTickValue: 20);
        await test.Db.SaveChangesAsync();
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        Assert.Equal("plague-test", (await rig.Statuses.RemoveFirstAsync(test.Room, "Character", [test.Character.Id], false))!.Code);
        Assert.True(await rig.Statuses.HasAsync(test.Room, "Character", test.Character.Id, "basic-plague-test"));
    }

    [Fact]
    public async Task DarkPlagueCatalogRejectsInvalidDeclarationsAndExportsIndependentCopies()
    {
        foreach (var fault in new Action<MonsterCombatOptions>[]
        {
            o => o.Profiles["fire-test"].PlaguePoison!.WindowRounds = 1,
            o => o.Profiles["fire-test"].PlaguePoison!.BreakLightDamagePercent = 0,
            o => o.Profiles["fire-test"].PlaguePoison!.TriggerHpPercent = 100,
            o => o.StatusEffects[0].Mechanic = BattleStatusMechanic.None,
            o => o.StatusEffects[0].IsDispellable = false,
            o => o.StatusEffects[2].MaxStacks = 2,
            o => o.StatusEffects[3].EffectType = "DamageOverTime"
        })
        {
            var options = PlagueTestOptions(); fault(options);
            Assert.Throws<InvalidOperationException>(() => new MonsterCombatCatalog(Options.Create(options)));
        }
        var catalog = new MonsterCombatCatalog(Options.Create(PlagueTestOptions()));
        catalog.ExportOptions().Profiles["fire-test"].PlaguePoison!.BasicPoisonStatusCodes.Clear();
        Assert.Single(catalog.ExportOptions().Profiles["fire-test"].PlaguePoison!.BasicPoisonStatusCodes);
    }

    private static async Task<FireRig> PlagueRigAsync(BattleTestContext test)
    {
        var rig = await FireRigAsync(test, PlagueTestOptions());
        foreach (var p in rig.Party) p.Character.MaxHp = 1000;
        test.Monster.Hp = 7000;
        return rig;
    }

    [Fact]
    public async Task DarkPlagueNewDefinitionDoesNotAddThePhaseToAnOldFrozenRoom()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 1000);
        var rig = await PlagueRigAsync(test);
        var oldOptions = PlagueTestOptions(); oldOptions.Profiles["fire-test"].PlaguePoison = null;
        var oldCatalog = new MonsterCombatCatalog(Options.Create(oldOptions));
        var depths = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions()));
        var rewards = RewardTestFactory.CreateCatalog();
        var oldRules = new DungeonRunRulesService(test.Db, oldCatalog, rewards, PartyScalingCatalog.Default, depths);
        var old = await oldRules.EnsureAsync(test.Room);
        await test.Db.SaveChangesAsync();
        var currentRules = new DungeonRunRulesService(test.Db, rig.Catalog, rewards, PartyScalingCatalog.Default, depths);
        var statuses = new BattleStatusService(test.Db, rig.Catalog.Statuses, runRules: currentRules);
        var phases = new MonsterPhaseService(test.Db, rig.Catalog, statuses, currentRules);
        await phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        Assert.Empty(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal(old.Revision, (await currentRules.EnsureAsync(test.Room)).Revision);
        var fresh = new Room { DungeonId=test.Room.DungeonId, MonsterId=test.Monster.Id, SlotCount=5 };
        test.Db.Rooms.Add(fresh); await test.Db.SaveChangesAsync();
        await phases.BeginRoundAsync(fresh, test.Monster, [], rig.Party);
        Assert.True(test.Db.BattleMonsterPhaseStates.Local.Single().IsActive);
        Assert.NotEqual(old.Revision, (await currentRules.EnsureAsync(fresh)).Revision);
    }

    private static MonsterCombatOptions PlagueTestOptions() => new()
    {
        StatusEffects =
        [
            new() { Code="plague-test", Name="Plague", Description="Plague", EffectType="DamageOverTime", ValuePerStack=1,
                MaxStacks=4, IsPositive=false, IsDispellable=true, Mechanic=BattleStatusMechanic.PlaguePoison },
            new() { Code="plague-target", Name="Target", Description="Target", EffectType="None", IsPositive=true,
                IsDispellable=false, IsHidden=true, Lifetime=BattleStatusLifetime.Encounter },
            new() { Code="plague-tick", Name="Tick", Description="Tick", EffectType="None", MaxStacks=4, IsPositive=true,
                IsDispellable=false, IsHidden=true, Lifetime=BattleStatusLifetime.Encounter },
            new() { Code="plague-reward", Name="Reward", Description="Reward", EffectType="ReductionPercent", ValuePerStack=15,
                IsPositive=true, IsDispellable=false },
            new() { Code="basic-plague-test", Name="Basic poison", Description="Basic poison", EffectType="DamageOverTime", ValuePerStack=1 }
        ],
        Profiles = new()
        {
            ["fire-test"] = new() { PlaguePoison = new() { TriggerHpPercent=70, WindowRounds=4, BreakLightDamagePercent=2,
                AttackPercentPerStack=10, PoisonStatusCode="plague-test", TargetStatusCode="plague-target", TickUsedStatusCode="plague-tick",
                RewardStatusCode="plague-reward", RewardRounds=3, BasicPoisonStatusCodes=["basic-plague-test"] } }
        }
    };
}
