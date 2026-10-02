using System.Collections.Immutable;
using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LightMirrorStartsAfterHpCrossingAndLastWindowRoundCanEarnFullReward(bool clear)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test, MirrorTestOptions());
        using var recording = rig.Damage.Events.Begin(test.Room, test.Monster, rig.Party);
        test.Room.RoundNumber = 60;
        test.Monster.Hp = 7001;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        Assert.Equal(0, await rig.Phases.ReflectionMirrorStacksAsync(test.Room, test.Monster));
        for (var round = 61; round <= 64; round++)
        {
            test.Room.RoundNumber = round;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            Assert.Equal(3, await rig.Phases.ReflectionMirrorStacksAsync(test.Room, test.Monster));
            if (clear && round == 64)
            {
                var removed = await rig.Statuses.RemoveFirstAsync(test.Room, "Monster", [test.Monster.Id], true);
                Assert.Equal("mirror-test", removed!.Code);
                await rig.Damage.ObserveDispelAsync(rig.Context, removed.Code);
                await rig.Damage.ObserveDispelAsync(rig.Context, removed.Code);
            }
            await MirrorEndAsync(test, rig);
            await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
        }
        for (var round = 65; round <= 68; round++)
        {
            test.Room.RoundNumber = round;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            Assert.Equal(0, await rig.Phases.ReflectionMirrorStacksAsync(test.Room, test.Monster));
            Assert.Equal(clear && round <= 67 ? 20m : 0m,
                await rig.Statuses.ModifierAsync(test.Room, "Monster", test.Monster.Id, "DamageTakenPercent"));
            await MirrorEndAsync(test, rig);
        }
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal(1, state.ActivationCount);
        Assert.Equal(clear ? 1 : 0, state.BreakCount);
        Assert.Equal(clear ? 0 : 1, state.ExpiryCount);
        Assert.False(state.IsActive);
    }

    [Fact]
    public async Task LightMirrorRawCapAndDarkDedupSurviveReloadWithRoundStartHpSnapshot()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 2000, characterAttack: 10000);
        test.Character.MaxHp = 2000;
        var rig = await FireRigAsync(test, MirrorTestOptions());
        using var recording = rig.Damage.Events.Begin(test.Room, test.Monster, rig.Party);
        test.Monster.Hp = 700000; test.Monster.MaxHp = 1000000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        var actor = BattleActor.ForCharacter(rig.Party[0]);
        await rig.Damage.CharacterDamageAsync(rig.Context, actor, BattleSkillEffect.Damage(100), BattleDamageOrigin.NormalAttack, false, damageElement: ElementType.Dark);
        Assert.Equal(1800, actor.Hp);
        await test.Db.SaveChangesAsync();
        await using var db = test.CreateDbContext();
        var room = await db.Rooms.SingleAsync(); var monster = await db.Monsters.SingleAsync();
        var character = await db.Characters.SingleAsync();
        // A later stat change must neither reset the budget nor alter its HP snapshot.
        character.MaxHp = 10000;
        var party = new[] { new BattleParticipant(await db.RoomSlots.SingleAsync(), character) };
        var statuses = new BattleStatusService(db, rig.Catalog.Statuses);
        var phases = new MonsterPhaseService(db, rig.Catalog, statuses);
        var damage = new BattleDamageService(statuses, new BattleGuardService(statuses), phases: phases);
        var context = new BattleExecutionContext(room, monster, party, new Dictionary<int, ElementType>(),
            new Dictionary<int, OperationPotionBonuses>(), []);
        await phases.BeginRoundAsync(room, monster, [], party);
        await damage.CharacterDamageAsync(context, BattleActor.ForCharacter(party[0]), BattleSkillEffect.Damage(100), BattleDamageOrigin.Skill, false, damageElement: ElementType.Dark);
        Assert.Equal(1800, character.Hp);
        Assert.Equal(2, await phases.ReflectionMirrorStacksAsync(room, monster));
        var budget = Assert.Single(await statuses.GetActiveAsync(room, "Character", [character.Id]), s => s.EffectCode == "mirror-budget");
        Assert.Equal(2000, budget.PerTickValue);
        Assert.Equal(200m, budget.MagnitudeSnapshot);
        Assert.Equal(1, (await db.BattleMonsterPhaseStates.SingleAsync()).ActivationCount);
    }

    [Fact]
    public async Task LightMirrorDarkDamageWeakensBeforeReflectionAndSharesTheLoweredCap()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 2000, characterAttack: 2500);
        test.Character.MaxHp = 2000;
        var rig = await FireRigAsync(test, MirrorTestOptions());
        using var recording = rig.Damage.Events.Begin(test.Room, test.Monster, rig.Party);
        test.Monster.Hp = 700000; test.Monster.MaxHp = 1000000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        var actor = BattleActor.ForCharacter(rig.Party[0]);
        await rig.Damage.CharacterDamageAsync(rig.Context, actor, BattleSkillEffect.Damage(100), BattleDamageOrigin.NormalAttack, false);
        Assert.Equal(1850, actor.Hp);
        await rig.Damage.CharacterDamageAsync(rig.Context, actor, BattleSkillEffect.Damage(100), BattleDamageOrigin.Skill, false, damageElement: ElementType.Dark);
        Assert.Equal(2, await rig.Phases.ReflectionMirrorStacksAsync(test.Room, test.Monster));
        Assert.Equal(1800, actor.Hp); // lowered cap 200 minus the already spent 150
        await rig.Damage.CharacterDamageAsync(rig.Context, actor, BattleSkillEffect.Damage(100), BattleDamageOrigin.Counter, false, damageElement: ElementType.Dark);
        Assert.Equal(2, await rig.Phases.ReflectionMirrorStacksAsync(test.Room, test.Monster));
        Assert.Equal(1800, actor.Hp);
        await MirrorEndAsync(test, rig);
        test.Room.RoundNumber++;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        await rig.Damage.CharacterDamageAsync(rig.Context, actor, BattleSkillEffect.Damage(100), BattleDamageOrigin.Mechanic, false, damageElement: ElementType.Dark);
        Assert.Equal(1, await rig.Phases.ReflectionMirrorStacksAsync(test.Room, test.Monster));
        Assert.Equal(1, Assert.Single(test.Db.BattleMonsterPhaseStates.Local).ActivationCount);
    }

    [Theory]
    [InlineData(34, 0, 198)]
    [InlineData(0, 40, 180)]
    public async Task LightMirrorAppliesExistingProtectionWithoutCounterPermission(int weaponDr, int guard, int expectedDamage)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 2000, characterAttack: 10000);
        test.Character.MaxHp = 2000;
        test.Character.CombatWeaponDirectReductionPercent = weaponDr;
        var rig = await FireRigAsync(test, MirrorTestOptions());
        using var recording = rig.Damage.Events.Begin(test.Room, test.Monster, rig.Party);
        test.Monster.Hp = 700000; test.Monster.MaxHp = 1000000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        if (guard > 0) await rig.Effects.Guards.ApplyAsync(test.Room, test.Character.Id, guard,
            new("Character", test.Character.Id, "knight-guard"), true);
        await rig.Damage.CharacterDamageAsync(rig.Context, BattleActor.ForCharacter(rig.Party[0]),
            BattleSkillEffect.Damage(100), BattleDamageOrigin.Skill, false);
        Assert.Equal(2000 - expectedDamage, test.Character.Hp);
        var reflected = Assert.Single(rig.Damage.Events.Snapshot(test.Room), e => e.SkillCode == "light-reflection");
        Assert.Equal(expectedDamage, reflected.ActualAmount);
        Assert.Null(reflected.Element);
        Assert.Equal(BattleActionKind.Mechanic, reflected.ActionKind);
        Assert.False(await rig.Effects.Guards.ConsumeCounterAsync(test.Room, test.Character.Id, test.Monster.Id));
    }

    [Fact]
    public async Task LightMirrorIgnoresZeroUnattributedDeadAndWrongElementDamage()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test, MirrorTestOptions());
        using var recording = rig.Damage.Events.Begin(test.Room, test.Monster, rig.Party); test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        await rig.Damage.ObserveDirectDamageAsync(rig.Context, ElementType.Dark, 0, test.Character.Id);
        await rig.Damage.ObserveDirectDamageAsync(rig.Context, ElementType.Dark, 100);
        await rig.Damage.ObserveDirectDamageAsync(rig.Context, ElementType.Dark, 100, -1);
        await rig.Damage.ObserveDirectDamageAsync(rig.Context, ElementType.Light, 100, test.Character.Id);
        test.Character.Hp = 0;
        await rig.Damage.ObserveDirectDamageAsync(rig.Context, ElementType.Dark, 100, test.Character.Id);
        Assert.Equal(3, await rig.Phases.ReflectionMirrorStacksAsync(test.Room, test.Monster));
        Assert.Equal(0, Assert.Single(test.Db.BattleMonsterPhaseStates.Local).LinkedHitCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LightMirrorBreakingActionCompletesDamageBeforeDispelAndHasNoReflection(bool darkBreak)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 2000, characterAttack: 10000);
        test.Character.MaxHp = 2000;
        var rig = await FireRigAsync(test, MirrorTestOptions());
        using var recording = rig.Damage.Events.Begin(test.Room, test.Monster, rig.Party);
        test.Monster.Hp = 700000; test.Monster.MaxHp = 1000000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        var context = darkBreak ? rig.Context with { MainWeaponElements = new Dictionary<int, ElementType> { [test.Character.Id] = ElementType.Dark } } : rig.Context;
        if (darkBreak)
        {
            for (var round = 0; round < 2; round++)
            {
                await rig.Damage.ObserveDirectDamageAsync(context, ElementType.Dark, 1, test.Character.Id);
                await MirrorEndAsync(test, rig); test.Room.RoundNumber++;
                await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            }
        }
        var skill = MirrorCastSkill(!darkBreak);
        var result = await rig.Effects.ExecuteAsync(new(context, skill, BattleActor.ForCharacter(rig.Party[0])));
        Assert.True(result.ActualDamage > 0);
        Assert.Equal(2000, test.Character.Hp);
        Assert.Equal(1, Assert.Single(test.Db.BattleMonsterPhaseStates.Local).BreakCount);
        Assert.DoesNotContain(rig.Damage.Events.Snapshot(test.Room), e => e.SkillCode == "light-reflection");
        if (!darkBreak)
        {
            var facts = rig.Damage.Events.Snapshot(test.Room).ToList();
            Assert.True(facts.FindIndex(e => e.Kind == BattleEventKind.Damage) < facts.FindIndex(e => e.Kind == BattleEventKind.Dispel));
        }
    }

    [Fact]
    public async Task LightMirrorNativeContinuousDispelRemovesNewPhaseBeforeActions()
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Character.ProfessionCode = "mage";
        var configuration = new ConfigurationBuilder().AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        var options = configuration.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!;
        var fixture = MirrorTestOptions();
        options.Profiles["fire-test"] = fixture.Profiles["fire-test"];
        options.Skills.AddRange(fixture.Skills);
        options.StatusEffects.AddRange(fixture.StatusEffects);
        var rig = await FireRigAsync(test, options);
        using var recording = rig.Damage.Events.Begin(test.Room, test.Monster, rig.Party);
        var continuous = rig.Catalog.Statuses.FindMechanic(BattleStatusMechanic.ContinuousDispel)!;
        await rig.Statuses.ApplyAsync(test.Room, "Character", test.Character.Id, continuous.Code, 1, [], "Mage");
        test.Room.RoundNumber = 1; test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        Assert.Equal(3, await rig.Phases.ReflectionMirrorStacksAsync(test.Room, test.Monster));
        await MageMechanics.RoundStartAsync(rig.Context, rig.Effects);
        Assert.Equal(0, await rig.Phases.ReflectionMirrorStacksAsync(test.Room, test.Monster));
        Assert.Equal(1, Assert.Single(test.Db.BattleMonsterPhaseStates.Local).BreakCount);
        Assert.Equal(2, Assert.Single(test.Db.BattleMonsterPhaseStates.Local).RewardStartsAtRound);
    }

    [Fact]
    public async Task LightMirrorBossDeathCancelsPendingReflectionAndDoesNotReward()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 2000, characterAttack: 10000);
        test.Character.MaxHp = 2000;
        var rig = await FireRigAsync(test, MirrorTestOptions());
        using var recording = rig.Damage.Events.Begin(test.Room, test.Monster, rig.Party); test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        await rig.Effects.ExecuteAsync(new(rig.Context, MirrorCastSkill(true), BattleActor.ForCharacter(rig.Party[0])));
        await MirrorEndAsync(test, rig);
        Assert.Equal(0, test.Monster.Hp); Assert.Equal(2000, test.Character.Hp);
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.False(state.IsActive); Assert.Null(state.RewardStartsAtRound);
        Assert.Equal(0, state.BreakCount); Assert.Equal(0, state.ExpiryCount);
        Assert.DoesNotContain(rig.Damage.Events.Snapshot(test.Room), e => e.SkillCode == "light-reflection");
    }

    [Fact]
    public async Task LightMirrorDeathStopsSubsequentDirectActions()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 2000, characterAttack: 10000);
        test.Character.MaxHp = 2000;
        var rig = await FireRigAsync(test, MirrorTestOptions());
        using var recording = rig.Damage.Events.Begin(test.Room, test.Monster, rig.Party);
        test.Monster.Hp = 700000; test.Monster.MaxHp = 1000000; test.Character.Hp = 1;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        var actor = BattleActor.ForCharacter(rig.Party[0]);
        await rig.Damage.CharacterDamageAsync(rig.Context, actor, BattleSkillEffect.Damage(100), BattleDamageOrigin.NormalAttack, false);
        Assert.Equal(0, actor.Hp); var hp = test.Monster.Hp;
        Assert.Equal(0, (await rig.Damage.CharacterDamageAsync(rig.Context, actor, BattleSkillEffect.Damage(100), BattleDamageOrigin.NormalAttack, false)).ActualAmount);
        Assert.False((await rig.Effects.ExecuteAsync(new(rig.Context, MirrorCastSkill(true), actor))).Applied);
        Assert.Equal(hp, test.Monster.Hp);
        Assert.Equal(0, Assert.Single(test.Db.BattleMonsterPhaseStates.Local).BreakCount);
    }

    [Fact]
    public async Task LightMirrorRoundExecutorStopsDoubleAttackAndEchoAfterReflectionDeath()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 10000);
        test.Character.MaxHp = 2000; test.Character.Hp = 1;
        test.Character.WeaponDoubleAttackChancePercent = 100;
        test.Character.WeaponNormalEchoPercent = 100;
        var rig = await FireRigAsync(test, MirrorTestOptions());
        test.Monster.MaxHp = 1000000; test.Monster.Hp = 700000;
        using var recording = rig.Damage.Events.Begin(test.Room, test.Monster, rig.Party);
        var executor = new BattleRoundExecutor(test.Db, ConsumableTestFactory.Create(), rig.Skills,
            rig.Statuses, rig.Effects, rig.Monster, random: new Random(7213));
        Assert.Equal(BattleRoundOutcome.PartyDefeated,
            await executor.ExecuteAsync(test.Room, test.Monster, rig.Party, [], []));
        var facts = rig.Damage.Events.Snapshot(test.Room);
        Assert.Single(facts, e => e.Kind == BattleEventKind.Damage && e.Source?.ActorType == "Character");
        Assert.DoesNotContain(facts, e => e.SkillCode == "normal-echo" || e.SkillCode == "fire-test-slash");
        Assert.Equal(690000, test.Monster.Hp);
        Assert.Equal(0, test.Character.Hp);
    }

    [Fact]
    public async Task LightMirrorActualNativeSpellbreakClearsAfterDamageAndDoesNotReflectItsCast()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 10000);
        test.Character.MaxHp = test.Character.Hp = 2000;
        test.Character.ProfessionCode = "mage"; test.Character.Level = 10;
        var configuration = new ConfigurationBuilder().AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        var options = configuration.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!;
        var fixture = MirrorTestOptions(); options.Profiles["fire-test"] = fixture.Profiles["fire-test"];
        options.Skills.AddRange(fixture.Skills); options.StatusEffects.AddRange(fixture.StatusEffects);
        var rig = await FireRigAsync(test, options);
        test.Monster.MaxHp = 1000000; test.Monster.Hp = 700000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        var skills = new SkillCatalog(Options.Create(configuration.GetSection(SkillOptions.SectionName).Get<SkillOptions>()!));
        var skill = skills.Resolve(test.Character, "mage-spellbreak")!;
        using var recording = rig.Damage.Events.Begin(test.Room, test.Monster, rig.Party);
        var result = await rig.Effects.ExecuteAsync(new(rig.Context, skill, BattleActor.ForCharacter(rig.Party[0])), new MageMechanics());
        Assert.True(result.ActualDamage > 0); Assert.Equal(2000, test.Character.Hp);
        Assert.Equal(1, Assert.Single(test.Db.BattleMonsterPhaseStates.Local).BreakCount);
        Assert.DoesNotContain(rig.Damage.Events.Snapshot(test.Room), e => e.SkillCode == "light-reflection");
        Assert.NotEmpty(await rig.Statuses.MechanicStatesAsync(test.Room, "Character", test.Character.Id, BattleStatusMechanic.ContinuousDispel));
    }

    [Fact]
    public async Task LightMirrorDoesNotAppearInOldFrozenRoomButFreshRoomUsesIt()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var options = MirrorTestOptions();
        var rig = await FireRigAsync(test, options); test.Monster.Hp = 7000;
        options = MirrorTestOptions(); options.Profiles["fire-test"].ReflectionMirror = null;
        var old = new MonsterCombatCatalog(Options.Create(options));
        var depths = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions()));
        var rewards = RewardTestFactory.CreateCatalog();
        var oldRules = new DungeonRunRulesService(test.Db, old, rewards, PartyScalingCatalog.Default, depths);
        var frozen = await oldRules.EnsureAsync(test.Room); await test.Db.SaveChangesAsync();
        var rules = new DungeonRunRulesService(test.Db, rig.Catalog, rewards, PartyScalingCatalog.Default, depths);
        var statuses = new BattleStatusService(test.Db, rig.Catalog.Statuses, runRules: rules);
        var phases = new MonsterPhaseService(test.Db, rig.Catalog, statuses, rules);
        await phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        Assert.Equal(0, await phases.ReflectionMirrorStacksAsync(test.Room, test.Monster));
        Assert.Equal(frozen.Revision, (await rules.EnsureAsync(test.Room)).Revision);
        var fresh = new Game.Shared.Models.Room { DungeonId = test.Room.DungeonId, MonsterId = test.Monster.Id, SlotCount = 5 };
        test.Db.Rooms.Add(fresh); await test.Db.SaveChangesAsync();
        await phases.BeginRoundAsync(fresh, test.Monster, [], rig.Party);
        Assert.Equal(3, await phases.ReflectionMirrorStacksAsync(fresh, test.Monster));
        Assert.NotEqual(frozen.Revision, (await rules.EnsureAsync(fresh)).Revision);
    }

    [Fact]
    public void LightMirrorRejectsInvalidDefinitionsAndCopiesFrozenDeclarations()
    {
        foreach (var change in new Action<MonsterCombatOptions>[]
        {
            o => o.Profiles["fire-test"].ReflectionMirror!.TriggerHpPercent = 100,
            o => o.Profiles["fire-test"].ReflectionMirror!.WindowRounds = 0,
            o => o.Profiles["fire-test"].ReflectionMirror!.ReflectPercentPerStack = 34,
            o => o.Profiles["fire-test"].ReflectionMirror!.MaxHpCapPercentPerStack = 34,
            o => o.StatusEffects.Single(s => s.Code == "mirror-test").IsDispellable = false,
            o => o.Profiles["fire-test"].ReflectionMirror!.BudgetStatusCode = "mirror-hit"
        })
        {
            var options = MirrorTestOptions(); change(options);
            Assert.Throws<InvalidOperationException>(() => new MonsterCombatCatalog(Options.Create(options)));
        }
        var source = MirrorTestOptions(); var catalog = new MonsterCombatCatalog(Options.Create(source));
        var export = catalog.ExportOptions();
        source.Profiles["fire-test"].ReflectionMirror!.TriggerHpPercent = 20;
        export.Profiles["fire-test"].ReflectionMirror!.InitialStacks = 1;
        Assert.Equal(70, catalog.FindProfile("fire-test")!.ReflectionMirror!.TriggerHpPercent);
        Assert.Equal(3, catalog.FindProfile("fire-test")!.ReflectionMirror!.InitialStacks);
    }

    private static async Task MirrorEndAsync(BattleTestContext test, FireRig rig)
    {
        await rig.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, []);
        await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
    }

    private static CharacterSkillDefinition MirrorCastSkill(bool dispel) => new()
    {
        Code = "mirror-cast", Name = "Mirror test", Description = "Mirror test", ProfessionCode = "mage",
        CooldownRounds = 0, InitialCooldownRounds = 0, Level = 1, IsShared = false,
        UnlockLevel = 1, Level2UnlockLevel = 1, Level3UnlockLevel = 1, AutoCondition = "Always",
        Effects = (dispel ? new[] { BattleSkillEffect.Damage(100),
            new BattleSkillEffect(BattleEffectKind.Dispel, new(BattleTargetSide.Opponent, BattleTargetSelection.Primary, false, false, "Enemy")) }
            : new[] { BattleSkillEffect.Damage(100) }).ToImmutableArray()
    };

    private static MonsterCombatOptions MirrorTestOptions()
    {
        var options = FireTestOptions(); options.Profiles["fire-test"].FireCore = null;
        options.Profiles["fire-test"].ReflectionMirror = new()
        {
            TriggerHpPercent = 70, WindowRounds = 4, InitialStacks = 3, RemovalElement = ElementType.Dark,
            ReflectPercentPerStack = 2, MaxHpCapPercentPerStack = 5, MirrorStatusCode = "mirror-test",
            HitUsedStatusCode = "mirror-hit", BudgetStatusCode = "mirror-budget", RewardStatusCode = "mirror-reward", RewardRounds = 3
        };
        options.StatusEffects.AddRange([
            new() { Code = "mirror-test", Name = "Mirror", Description = "Mirror", EffectType = "None", MaxStacks = 3,
                InitialStacks = 3, CounterKind = BattleStatusCounterKind.Stacks, IsPositive = true, IsDispellable = true, Lifetime = BattleStatusLifetime.Rounds },
            new() { Code = "mirror-hit", Name = "Hit", Description = "Hit", EffectType = "None", IsPositive = true, IsDispellable = false, IsHidden = true, Lifetime = BattleStatusLifetime.CurrentRound },
            new() { Code = "mirror-budget", Name = "Budget", Description = "Budget", EffectType = "None", IsPositive = true, IsDispellable = false, IsHidden = true, Lifetime = BattleStatusLifetime.CurrentRound },
            new() { Code = "mirror-reward", Name = "Reward", Description = "Reward", EffectType = "DamageTakenPercent", ValuePerStack = 20, IsPositive = false, IsDispellable = false, Lifetime = BattleStatusLifetime.Rounds }
        ]);
        return options;
    }
}
