using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Dtos;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FireCoreHpTriggerGrantsFourFullRoundsAndNeverRepeatsAfterReload(bool broken)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var options = FireTestOptions();
        options.Profiles["fire-test"].FireCore = new()
        {
            TriggerHpPercent = 65, WindowRounds = 4, BreakWaterDamagePercent = 2.5m,
            HeatingStatusCode = "fire-test-heat", RewardStatusCode = "fire-test-reward", RewardRounds = 3
        };
        var rig = await FireRigAsync(test, options);
        test.Room.RoundNumber = 60;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        test.Monster.Hp = 6501;
        test.Room.RoundNumber++;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        Assert.False(await rig.Phases.WillBeHeatedAsync(test.Room, test.Monster));
        test.Monster.Hp = 6500;
        Assert.True(await rig.Phases.WillBeHeatedAsync(test.Room, test.Monster));
        // Crossing the boundary mid-round cannot consume a partial handling window.
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal(0, state.ActivationCount);
        for (var round = 62; round <= 65; round++)
        {
            test.Room.RoundNumber = round;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
            Assert.True(state.IsActive);
            Assert.Equal(25m, await rig.Statuses.ModifierAsync(test.Room, "Monster", test.Monster.Id, "AttackPercent"));
            if (broken)
                await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Water, round == 65 ? 100 : 50);
            await rig.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, []);
            await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
        }
        Assert.False(state.IsActive);
        Assert.Equal(1, state.ActivationCount);
        Assert.Equal(broken ? 1 : 0, state.BreakCount);
        Assert.Equal(broken ? 0 : 1, state.ExpiryCount);
        Assert.Equal(0, state.LinkedHitCount);
        for (var round = 66; round <= 69; round++)
        {
            test.Room.RoundNumber = round;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
            Assert.Equal(broken && round <= 68 ? 120 : 100,
                await rig.Statuses.AmplifyDamageAsync(test.Room, test.Monster.Id, 100));
            Assert.Equal(0m, await rig.Statuses.ModifierAsync(test.Room, "Monster", test.Monster.Id, "AttackPercent"));
            await rig.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, []);
        }
        await test.Db.SaveChangesAsync();
        await using var db = test.CreateDbContext();
        var room = await db.Rooms.SingleAsync();
        var monster = await db.Monsters.SingleAsync();
        room.RoundNumber = 100;
        var phases = new MonsterPhaseService(db, rig.Catalog, new BattleStatusService(db, rig.Catalog.Statuses));
        await phases.BeginRoundAsync(room, monster, []);
        Assert.False(await phases.WillBeHeatedAsync(room, monster));
        Assert.Equal(1, (await db.BattleMonsterPhaseStates.SingleAsync()).ActivationCount);
        Assert.Single(rig.Catalog.GetAddedMechanics("fire-test", 1));
    }

    [Fact]
    public void FireCoreHpTriggerRejectsMixedSchedulesAndPartialSplashConfiguration()
    {
        foreach (var fault in new Action<FireCoreOptions>[]
        {
            c => c.TriggerHpPercent = 0,
            c => c.TriggerHpPercent = 100,
            c => c.FirstActivationRound = 6,
            c => c.CycleRounds = 10,
            c => c.LinkedSkillCode = "fire-test-slash",
            c => c.ExtraAttackPowerPercent = 80
        })
        {
            var options = FireTestOptions();
            var core = new FireCoreOptions { TriggerHpPercent = 65, WindowRounds = 4, BreakWaterDamagePercent = 2.5m,
                HeatingStatusCode = "fire-test-heat", RewardStatusCode = "fire-test-reward", RewardRounds = 3 };
            fault(core);
            options.Profiles["fire-test"].FireCore = core;
            Assert.Throws<InvalidOperationException>(() => new MonsterCombatCatalog(Options.Create(options)));
        }
    }

    [Fact]
    public async Task FireCoreUsesBossLocalClockFourOpportunitiesAndThreeFullRewardRounds()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test);
        test.Room.RoundNumber = 60;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        for (var local = 2; local <= 26; local++)
        {
            test.Room.RoundNumber = 60 + local - 1;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []); // Duplicate preparation is harmless.
            if (local is 6 or 16 or 26) Assert.True(state.IsActive);
            if (local is 6 or 7 or 8)
                await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Water, 175);
            if (local == 9)
            {
                Assert.True(state.IsActive);
                await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Water, 175);
                Assert.False(state.IsActive);
                Assert.Equal(0m, await rig.Statuses.ModifierAsync(test.Room, "Monster", test.Monster.Id, "AttackPercent"));
                Assert.Equal(100, await rig.Statuses.AmplifyDamageAsync(test.Room, test.Monster.Id, 100));
            }
            if (local is 10 or 11 or 12)
                Assert.Equal(120, await rig.Statuses.AmplifyDamageAsync(test.Room, test.Monster.Id, 100));
            if (local == 13) Assert.Equal(100, await rig.Statuses.AmplifyDamageAsync(test.Room, test.Monster.Id, 100));
            await rig.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, []);
            await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
        }
        Assert.Equal(3, state.ActivationCount);
        Assert.Equal(1, state.BreakCount);
        Assert.Equal(1, state.ExpiryCount);
        Assert.Equal(36, state.NextActivationRound);
    }

    [Fact]
    public async Task FireCoreEarlyBreakKeepsFixedCycleAndTimeoutGrantsNoReward()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test);
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        test.Room.RoundNumber = 5;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Water, 700);
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal(16, state.NextActivationRound);
        test.Room.RoundNumber = 15;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        for (var round = 15; round <= 18; round++)
        {
            test.Room.RoundNumber = round;
            Assert.True(await rig.Phases.IsHeatedAsync(test.Room, test.Monster));
            await rig.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, []);
            await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
        }
        Assert.False(state.IsActive);
        Assert.Equal(1, state.ExpiryCount);
        Assert.Equal(1, state.BreakCount);
        test.Room.RoundNumber = 19;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        Assert.Equal(0m, await rig.Statuses.ModifierAsync(test.Room, "Monster", test.Monster.Id, "AttackPercent"));
        Assert.Equal(100, await rig.Statuses.AmplifyDamageAsync(test.Room, test.Monster.Id, 100));
    }

    [Theory]
    [InlineData(BattleDamageOrigin.NormalAttack)]
    [InlineData(BattleDamageOrigin.Skill)]
    [InlineData(BattleDamageOrigin.Counter)]
    [InlineData(BattleDamageOrigin.Mechanic)]
    public async Task FireCoreCountsActualWaterDirectDamageThroughTheDamagePipeline(BattleDamageOrigin origin)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        var rig = await FireRigAsync(test);
        await FireActivateAsync(test, rig);
        var source = BattleActor.ForCharacter(rig.Party[0]);
        await rig.Damage.CharacterDamageAsync(rig.Context, source, BattleSkillEffect.Damage(100), origin, false,
            damageElement: ElementType.Fire);
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal(0, state.WaterDamage);
        var result = await rig.Damage.CharacterDamageAsync(rig.Context, source, BattleSkillEffect.Damage(100), origin, false,
            damageElement: ElementType.Water);
        Assert.Equal(result.ActualAmount, state.WaterDamage);
        test.Monster.Hp = 7;
        var overkill = await rig.Damage.CharacterDamageAsync(rig.Context, source, BattleSkillEffect.Damage(1000), origin, false,
            damageElement: ElementType.Water);
        Assert.Equal(7, overkill.ActualAmount);
        Assert.Equal(result.ActualAmount + 7, state.WaterDamage);
        Assert.Equal(0, state.BreakCount); // Calculated overkill cannot pass the 700 damage threshold.
    }

    [Fact]
    public async Task FireCoreIgnoresPeriodicDamageAndNormalEchoCanBreakIt()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 400);
        var rig = await FireRigAsync(test);
        test.Character.WeaponNormalEchoPercent = 100;
        test.Db.CharacterWeapons.Add(new CharacterWeapon
        {
            CharacterId = test.Character.Id, EquippedSlotIndex = 1, WeaponCode = "test-water", Name = "Water", Element = ElementType.Water, MaxHp = 1
        });
        await test.Db.SaveChangesAsync();
        await FireActivateAsync(test, rig);
        await rig.Statuses.ApplyAsync(test.Room, "Monster", test.Monster.Id, "fire-test-dot", 3, [], "Boss", perTickValue: 800);
        test.Room.RoundNumber = 6;
        await rig.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, []);
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal(0, state.WaterDamage);
        var executor = new BattleRoundExecutor(test.Db, ConsumableTestFactory.Create(), rig.Skills,
            rig.Statuses, rig.Effects, rig.Monster);
        using var recording = rig.Statuses.Events.Begin(test.Room, test.Monster, rig.Party);
        using var settlement = await rig.Statuses.BeginSettlementAsync(test.Room);
        await executor.ExecuteAsync(test.Room, test.Monster, rig.Party, [], []);
        var damage = rig.Statuses.Events.Snapshot(test.Room).Where(e => e.Kind == BattleEventKind.Damage && e.Target.ActorType == "Monster").ToList();
        Assert.Contains(damage, e => e.SkillCode == "normal-echo");
        Assert.Equal(1, state.BreakCount);
        Assert.Equal(damage.Where(e => e.Element == ElementType.Water && e.ActionKind != BattleActionKind.Periodic).Sum(e => (long)e.ActualAmount), state.WaterDamage);
    }

    [Theory]
    [InlineData(false, false, false, 3)]
    [InlineData(false, true, false, 0)]
    [InlineData(false, false, true, 1)]
    [InlineData(true, false, false, 3)]
    [InlineData(true, true, false, 0)]
    [InlineData(true, false, true, 1)]
    public async Task FireCoreLinkedSlashTargetsDistinctLivingOthersAndCancelsOnInterruptOrBreak(bool hpTriggered, bool interrupted, bool broken, int expectedHits)
    {
        await using var test = await BattleTestContext.CreateAsync();
        for (var slot = 2; slot <= 5; slot++) await test.AddSlotAsync(slot, $"Actor{slot}", hp: 10000);
        var options = FireTestOptions();
        if (hpTriggered)
        {
            var core = options.Profiles["fire-test"].FireCore!;
            core.TriggerHpPercent = 65;
            core.FirstActivationRound = core.CycleRounds = 0;
            core.BreakWaterDamagePercent = 5;
        }
        var rig = await FireRigAsync(test, options);
        foreach (var actor in rig.Party) actor.Character.Hp = actor.Character.MaxHp = 10000;
        rig.Party[^1].Character.Hp = 0;
        await test.Db.SaveChangesAsync();
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        test.Room.RoundNumber = 5;
        if (hpTriggered) test.Monster.Hp = 6500;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        var intent = await rig.Monster.EnsureIntentAsync(test.Room, test.Monster);
        intent.IsInterrupted = interrupted;
        if (broken) await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Water, hpTriggered ? 500 : 700);
        using var recording = rig.Statuses.Events.Begin(test.Room, test.Monster, rig.Party);
        using var settlement = await rig.Statuses.BeginSettlementAsync(test.Room);
        await rig.Monster.ExecuteIntentAsync(test.Room, test.Monster, rig.Party, rig.Context.MainWeaponElements, []);
        var hits = rig.Statuses.Events.Snapshot(test.Room).Where(e => e.Kind == BattleEventKind.Damage && e.Source?.ActorType == "Monster").ToList();
        Assert.Equal(expectedHits, hits.Count);
        Assert.Equal(expectedHits, hits.Select(e => e.Target.ActorId).Distinct().Count());
        Assert.DoesNotContain(hits, e => e.Target.ActorId == rig.Party[^1].Character.Id);
        if (expectedHits == 3)
        {
            Assert.Equal(200, hits[0].CalculatedAmount); // 100 attack * 125% heating * 160% slash.
            Assert.All(hits.Skip(1), e => Assert.Equal(100, e.CalculatedAmount));
            Assert.All(hits.Skip(1), e => Assert.Equal("fire-core-splash", e.SkillCode));
        }
    }

    [Fact]
    public async Task FireCoreStateSurvivesServiceRecreationAndResetsForTheNextRun()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test);
        await FireActivateAsync(test, rig);
        await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Water, 350);
        await test.Db.SaveChangesAsync();
        await using var db = test.CreateDbContext();
        var room = await db.Rooms.SingleAsync();
        var monster = await db.Monsters.SingleAsync();
        var statuses = new BattleStatusService(db, rig.Catalog.Statuses);
        var phases = new MonsterPhaseService(db, rig.Catalog, statuses);
        var context = rig.Context with { Room = room, Monster = monster };
        await phases.BeginRoundAsync(room, monster, []);
        await phases.ObserveDirectDamageAsync(context, ElementType.Water, 350);
        await db.SaveChangesAsync();
        var state = await db.BattleMonsterPhaseStates.SingleAsync();
        Assert.Equal(1, state.ActivationCount);
        Assert.Equal(1, state.BreakCount);
        Assert.False(state.IsActive);
        var service = new MonsterCombatService(db, rig.Catalog, statuses: statuses, phases: phases);
        await service.ResetRoomStateAsync(room.Id);
        await db.SaveChangesAsync();
        Assert.Empty(await db.BattleMonsterPhaseStates.ToListAsync());
        room.RunSequence++;
        room.RoundNumber = 0;
        await phases.BeginRoundAsync(room, monster, []);
        await db.SaveChangesAsync();
        state = await db.BattleMonsterPhaseStates.SingleAsync();
        Assert.Equal(0, state.ActivationCount);
        Assert.Equal(room.RunSequence, state.RunSequence);
    }

    [Fact]
    public async Task FireCoreIntentPreviewShowsUpcomingSplashWithoutStartingTheEncounterClock()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test);
        await rig.Monster.GetIntentResponseAsync(test.Room, test.Monster);
        Assert.Empty(test.Db.BattleMonsterPhaseStates.Local);
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        test.Room.RoundNumber = 5;
        var preview = await rig.Monster.GetIntentResponseAsync(test.Room, test.Monster);
        Assert.Contains("另随机攻击2名", preview!.Description);
        Assert.Equal(0, Assert.Single(test.Db.BattleMonsterPhaseStates.Local).ActivationCount);
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Water, 700);
        preview = await rig.Monster.GetIntentResponseAsync(test.Room, test.Monster);
        Assert.DoesNotContain("另随机攻击", preview!.Description);
    }

    [Fact]
    public void FireCoreConfigurationRejectsInvalidWindowsAndBrokenReferences()
    {
        foreach (var fault in new Action<MonsterCombatOptions>[]
        {
            o => o.Profiles["fire-test"].FireCore!.WindowRounds = 0,
            o => o.Profiles["fire-test"].FireCore!.CycleRounds = 7,
            o => o.Profiles["fire-test"].FireCore!.BreakWaterDamagePercent = 0,
            o => o.Profiles["fire-test"].FireCore!.LinkedSkillCode = "missing",
            o => o.StatusEffects[0].IsDispellable = true,
            o => o.StatusEffects[1].EffectType = "AttackPercent"
        })
        {
            var options = FireTestOptions();
            fault(options);
            Assert.Throws<InvalidOperationException>(() => new MonsterCombatCatalog(Options.Create(options)));
        }
    }

    private static async Task FireActivateAsync(BattleTestContext test, FireRig rig)
    {
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        test.Room.RoundNumber = 5;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
    }

    [Fact]
    public async Task FireCoreNewDeclarationsDoNotChangeAnOldFrozenRoom()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test);
        var oldOptions = FireTestOptions();
        oldOptions.Profiles["fire-test"].FireCore = null;
        var oldCatalog = new MonsterCombatCatalog(Options.Create(oldOptions));
        var depths = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions()));
        var rewards = RewardTestFactory.CreateCatalog();
        var oldRules = new DungeonRunRulesService(test.Db, oldCatalog, rewards, PartyScalingCatalog.Default, depths);
        var oldDefinition = await oldRules.EnsureAsync(test.Room);
        await test.Db.SaveChangesAsync();
        var newRules = new DungeonRunRulesService(test.Db, rig.Catalog, rewards, PartyScalingCatalog.Default, depths);
        var statuses = new BattleStatusService(test.Db, rig.Catalog.Statuses, runRules: newRules);
        var phases = new MonsterPhaseService(test.Db, rig.Catalog, statuses, newRules);
        await phases.BeginRoundAsync(test.Room, test.Monster, []);
        test.Room.RoundNumber = 5;
        await phases.BeginRoundAsync(test.Room, test.Monster, []);
        Assert.Empty(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal(oldDefinition.Revision, (await newRules.EnsureAsync(test.Room)).Revision);
        var freshRoom = new Room { DungeonId = test.Room.DungeonId, MonsterId = test.Monster.Id, SlotCount = 5 };
        test.Db.Rooms.Add(freshRoom);
        await test.Db.SaveChangesAsync();
        await phases.BeginRoundAsync(freshRoom, test.Monster, []);
        freshRoom.RoundNumber = 5;
        await phases.BeginRoundAsync(freshRoom, test.Monster, []);
        Assert.True(await phases.IsHeatedAsync(freshRoom, test.Monster));
        Assert.NotEqual(oldDefinition.Revision, (await newRules.EnsureAsync(freshRoom)).Revision);
    }

    private static async Task<FireRig> FireRigAsync(BattleTestContext test, MonsterCombatOptions? options = null)
    {
        test.Monster.Hp = test.Monster.MaxHp = test.Monster.BaseMaxHp = 10000;
        test.Monster.Attack = 100;
        test.Monster.Defense = 0;
        test.Monster.CombatProfileCode = "fire-test";
        var party = (await test.Db.RoomSlots.OrderBy(s => s.SlotIndex).ToListAsync())
            .Select(s => new BattleParticipant(s, test.Db.Characters.Local.Single(c => c.Id == s.CharacterId))).ToList();
        var catalog = new MonsterCombatCatalog(Options.Create(options ?? FireTestOptions()));
        var skills = new SkillCatalog(Options.Create(new SkillOptions()));
        var statuses = new BattleStatusService(test.Db, catalog.Statuses);
        var phases = new MonsterPhaseService(test.Db, catalog, statuses);
        var guards = new BattleGuardService(statuses);
        var damage = new BattleDamageService(statuses, guards, new Random(7213), phases: phases);
        var effects = new BattleEffectExecutor(skills, statuses, guards, damage);
        var monster = new MonsterCombatService(test.Db, catalog, new Random(7213), statuses, skills, effects, phases: phases);
        return new(catalog, skills, statuses, phases, damage, effects, monster, party,
            new(test.Room, test.Monster, party, new Dictionary<int, ElementType>(), new Dictionary<int, OperationPotionBonuses>(), []));
    }

    private sealed record FireRig(MonsterCombatCatalog Catalog, SkillCatalog Skills, BattleStatusService Statuses,
        MonsterPhaseService Phases, BattleDamageService Damage, BattleEffectExecutor Effects, MonsterCombatService Monster,
        List<BattleParticipant> Party, BattleExecutionContext Context);

    private static MonsterCombatOptions FireTestOptions() => new()
    {
        StatusEffects =
        [
            new() { Code = "fire-test-heat", Name = "Heat", Description = "Heat", EffectType = "AttackPercent", ValuePerStack = 25, IsPositive = true, IsDispellable = false },
            new() { Code = "fire-test-reward", Name = "Reward", Description = "Reward", EffectType = "DamageTakenPercent", ValuePerStack = 20, IsPositive = false, IsDispellable = false },
            new() { Code = "fire-test-dot", Name = "Dot", Description = "Dot", EffectType = "DamageOverTime", ValuePerStack = 800 }
        ],
        Skills = [new() { Code = "fire-test-slash", Name = "Slash", Description = "Slash", DamagePowerPercent = 160, ForcedPriority = 10, CooldownRounds = 3 }],
        Profiles = new()
        {
            ["fire-test"] = new()
            {
                Skills = [new() { Code = "fire-test-slash" }],
                FireCore = new()
                {
                    FirstActivationRound = 6, CycleRounds = 10, WindowRounds = 4, BreakWaterDamagePercent = 7,
                    HeatingStatusCode = "fire-test-heat", RewardStatusCode = "fire-test-reward", RewardRounds = 3,
                    LinkedSkillCode = "fire-test-slash", ExtraTargetCount = 2, ExtraAttackPowerPercent = 80
                }
            }
        }
    };
}

public sealed class MonsterPhaseMigrationTests
{
    [Fact]
    public async Task MigrationMatchesModelPreservesExistingDataAndCanBeRolledBack()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options;
        await using var db = new GameDbContext(options);
        await db.GetService<IMigrator>().MigrateAsync("20260930030000_AddCoopDropBonusEvidence");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO RewardEvents (RoomId, Sequence, EventKey, CoopParticipantCount, CoopDropBonusPercent) VALUES (7, 3, 'saved', 2, '10')");
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.BattleMonsterPhaseStates.ToListAsync());
        Assert.Equal("saved", (await db.RewardEvents.SingleAsync()).EventKey);
        await db.GetService<IMigrator>().MigrateAsync("20260930030000_AddCoopDropBonusEvidence");
        Assert.Equal("saved", (await db.RewardEvents.SingleAsync()).EventKey);
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
