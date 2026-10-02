using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LightLv3GrowthNeverRepairsStrengthAndBothDarkRoutesEarnThreeFullRounds(int darkActors)
    {
        await using var test = await BattleTestContext.CreateAsync();
        if (darkActors == 2) await test.AddSlotAsync(2, "Second");
        var rig = await FireRigAsync(test, MirrorLv3Options()); test.Monster.Hp = 7000;
        var clearRound = darkActors == 0 ? (int?)null : darkActors == 1 ? 3 : 1;
        for (var r = 0; r < 4; r++)
        {
            test.Room.RoundNumber = r;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            var active = clearRound is null || r <= clearRound;
            Assert.Equal(active ? 4 - r * darkActors : 0,
                await rig.Phases.ReflectionMirrorStacksAsync(test.Room, test.Monster));
            if (active)
            {
                Assert.Equal((2m + .5m * r, 5m + r), await rig.Phases.ReflectionMirrorRatesAsync(test.Room, test.Monster));
                foreach (var p in rig.Party.Take(darkActors))
                {
                    await rig.Damage.ObserveDirectDamageAsync(rig.Context, ElementType.Dark, 1, p.Character.Id);
                    await rig.Damage.ObserveDirectDamageAsync(rig.Context, ElementType.Dark, 1, p.Character.Id);
                }
            }
            Assert.Equal(active ? Math.Max(0, 4 - (r + 1) * darkActors) : 0,
                await rig.Phases.ReflectionMirrorStacksAsync(test.Room, test.Monster));
            Assert.Equal(clearRound is { } cleared && r > cleared && r <= cleared + 3 ? 20m : 0m,
                await rig.Statuses.ModifierAsync(test.Room, "Monster", test.Monster.Id, "DamageTakenPercent"));
            await MirrorEndAsync(test, rig);
            await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
            await test.Db.SaveChangesAsync();
            await using var db = test.CreateDbContext();
            var room = await db.Rooms.SingleAsync(); var monster = await db.Monsters.SingleAsync();
            var statuses = new BattleStatusService(db, rig.Catalog.Statuses);
            var phases = new MonsterPhaseService(db, rig.Catalog, statuses);
            var expectedGrowth = active && (clearRound is null || r < clearRound) && r < 3 ? r + 1 : 0;
            Assert.Equal(expectedGrowth, await statuses.StacksAsync(room, "Monster", monster.Id, "mirror-amplification"));
            await phases.EndRoundAsync(room, monster, []); // duplicate settlement after service recreation
            Assert.Equal(expectedGrowth, await statuses.StacksAsync(room, "Monster", monster.Id, "mirror-amplification"));
            await db.SaveChangesAsync();
        }
        for (var r = 4; r <= 7; r++)
        {
            test.Room.RoundNumber = r;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            Assert.Equal(0, await rig.Phases.ReflectionMirrorStacksAsync(test.Room, test.Monster));
            Assert.Equal(clearRound is { } cleared && r > cleared && r <= cleared + 3 ? 20m : 0m,
                await rig.Statuses.ModifierAsync(test.Room, "Monster", test.Monster.Id, "DamageTakenPercent"));
            await MirrorEndAsync(test, rig);
        }
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal(1, state.ActivationCount);
        Assert.Equal(darkActors == 0 ? 0 : 1, state.BreakCount);
        Assert.Equal(darkActors == 0 ? 1 : 0, state.ExpiryCount);
        Assert.Equal(darkActors == 0 ? 0 : 4, state.LinkedHitCount);
    }

    [Fact]
    public async Task LightLv3RawReflectionAndBudgetIncreaseEachRoundWithoutChangingStrength()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100000);
        test.Character.MaxHp = test.Character.Hp = 2000;
        var rig = await FireRigAsync(test, MirrorLv3Options());
        test.Monster.MaxHp = 1000000; test.Monster.Hp = 700000;
        var actor = BattleActor.ForCharacter(rig.Party[0]);
        for (var r = 0; r < 4; r++)
        {
            test.Room.RoundNumber = r; actor.Hp = 2000;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            Assert.Equal(800m + 200m * r, await rig.Phases.ReflectionMirrorRawAsync(rig.Context, 10000));
            await rig.Damage.CharacterDamageAsync(rig.Context, actor, BattleSkillEffect.Damage(100), BattleDamageOrigin.NormalAttack, false);
            var expectedRaw = 400 + 80 * r;
            Assert.Equal(2000 - expectedRaw, actor.Hp);
            Assert.Equal(4, await rig.Phases.ReflectionMirrorStacksAsync(test.Room, test.Monster));
            var budget = Assert.Single(await rig.Statuses.GetActiveAsync(test.Room, "Character", [actor.Id]), s => s.EffectCode == "mirror-budget");
            Assert.Equal(expectedRaw, budget.MagnitudeSnapshot);
            await MirrorEndAsync(test, rig);
        }
        Assert.False(Assert.Single(test.Db.BattleMonsterPhaseStates.Local).IsActive);
        Assert.False(await rig.Statuses.HasAsync(test.Room, "Monster", test.Monster.Id, "mirror-amplification"));
    }

    [Fact]
    public async Task LightLv3DarkHitLowersAmplifiedCapBeforeItsOwnReflectionAndKeepsUsedBudget()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 2500);
        test.Character.MaxHp = test.Character.Hp = 2000;
        var rig = await FireRigAsync(test, MirrorLv3Options());
        test.Monster.MaxHp = 1000000; test.Monster.Hp = 700000; test.Monster.Element = ElementType.Light;
        for (var r = 0; r < 2; r++)
        {
            test.Room.RoundNumber = r;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            await MirrorEndAsync(test, rig);
        }
        test.Room.RoundNumber = 2;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        var actor = BattleActor.ForCharacter(rig.Party[0]);
        await rig.Damage.CharacterDamageAsync(rig.Context, actor, BattleSkillEffect.Damage(100), BattleDamageOrigin.NormalAttack, false);
        Assert.Equal(1700, actor.Hp);
        await rig.Damage.CharacterDamageAsync(rig.Context, actor, BattleSkillEffect.Damage(100), BattleDamageOrigin.Skill, false, damageElement: ElementType.Dark);
        Assert.Equal(1580, actor.Hp); // new cap 3 * 7% * 2000 = 420; earlier raw usage = 300
        Assert.Equal(3, await rig.Phases.ReflectionMirrorStacksAsync(test.Room, test.Monster));
        await rig.Damage.CharacterDamageAsync(rig.Context, actor, BattleSkillEffect.Damage(100), BattleDamageOrigin.Counter, false, damageElement: ElementType.Dark);
        Assert.Equal(1580, actor.Hp);
        Assert.Equal(3, await rig.Phases.ReflectionMirrorStacksAsync(test.Room, test.Monster));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LightLv3DispelClearsAmplificationAndReflectionTogether(bool continuous)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 10000);
        test.Character.MaxHp = test.Character.Hp = 2000;
        test.Character.ProfessionCode = "mage";
        var configuration = new ConfigurationBuilder().AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        var options = configuration.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!;
        var fixture = MirrorLv3Options(); options.Profiles["fire-test"] = fixture.Profiles["fire-test"];
        options.Skills.AddRange(fixture.Skills); options.StatusEffects.AddRange(fixture.StatusEffects);
        var rig = await FireRigAsync(test, options); test.Monster.MaxHp = 1000000; test.Monster.Hp = 700000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        if (continuous)
        {
            var status = rig.Catalog.Statuses.FindMechanic(BattleStatusMechanic.ContinuousDispel)!;
            await rig.Statuses.ApplyAsync(test.Room, "Character", test.Character.Id, status.Code, 1, [], "Mage");
        }
        await MirrorEndAsync(test, rig);
        test.Room.RoundNumber = 1;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        Assert.Equal(1, await rig.Statuses.StacksAsync(test.Room, "Monster", test.Monster.Id, "mirror-amplification"));
        if (continuous) await MageMechanics.RoundStartAsync(rig.Context, rig.Effects);
        else await rig.Effects.ExecuteAsync(new(rig.Context, MirrorCastSkill(true), BattleActor.ForCharacter(rig.Party[0])));
        Assert.Equal(2000, test.Character.Hp);
        Assert.Equal(0, await rig.Phases.ReflectionMirrorStacksAsync(test.Room, test.Monster));
        Assert.False(await rig.Statuses.HasAsync(test.Room, "Monster", test.Monster.Id, "mirror-amplification"));
        await MirrorEndAsync(test, rig);
        Assert.Equal(1, Assert.Single(test.Db.BattleMonsterPhaseStates.Local).BreakCount);
    }

    [Fact]
    public async Task LightLv3BossDeathClearsGrowthWithoutRewardOrExpiry()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test, MirrorLv3Options()); test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        await MirrorEndAsync(test, rig);
        Assert.Equal(1, await rig.Statuses.StacksAsync(test.Room, "Monster", test.Monster.Id, "mirror-amplification"));
        test.Room.RoundNumber = 1; test.Monster.Hp = 0;
        await MirrorEndAsync(test, rig);
        Assert.False(await rig.Statuses.HasAsync(test.Room, "Monster", test.Monster.Id, "mirror-amplification"));
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.False(state.IsActive); Assert.Null(state.RewardStartsAtRound);
        Assert.Equal(0, state.BreakCount); Assert.Equal(0, state.ExpiryCount);
    }

    [Fact]
    public async Task LightLv3DeclarationsKeepFrozenLv2ReflectionFixed()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test, MirrorLv3Options()); test.Monster.Hp = 7000;
        var old = new MonsterCombatCatalog(Options.Create(MirrorTestOptions()));
        var depths = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions()));
        var rewards = RewardTestFactory.CreateCatalog();
        var oldRules = new DungeonRunRulesService(test.Db, old, rewards, PartyScalingCatalog.Default, depths);
        var frozen = await oldRules.EnsureAsync(test.Room); await test.Db.SaveChangesAsync();
        var rules = new DungeonRunRulesService(test.Db, rig.Catalog, rewards, PartyScalingCatalog.Default, depths);
        var statuses = new BattleStatusService(test.Db, rig.Catalog.Statuses, runRules: rules);
        var phases = new MonsterPhaseService(test.Db, rig.Catalog, statuses, rules);
        for (var r = 0; r < 4; r++)
        {
            test.Room.RoundNumber = r;
            await phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            Assert.Equal(3, await phases.ReflectionMirrorStacksAsync(test.Room, test.Monster));
            Assert.Equal((2m, 5m), await phases.ReflectionMirrorRatesAsync(test.Room, test.Monster));
            await statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, []);
            await phases.EndRoundAsync(test.Room, test.Monster, []);
        }
        Assert.Equal(frozen.Revision, (await rules.EnsureAsync(test.Room)).Revision);
        Assert.False(await statuses.HasAsync(test.Room, "Monster", test.Monster.Id, "mirror-amplification"));
    }

    [Fact]
    public void LightLv3RejectsPartialGrowthOverflowAndDispellableAmplification()
    {
        foreach (var fault in new Action<MonsterCombatOptions>[]
        {
            o => o.Profiles["fire-test"].ReflectionMirror!.GrowthRounds = 4,
            o => o.Profiles["fire-test"].ReflectionMirror!.GrowthRounds = -1,
            o => o.Profiles["fire-test"].ReflectionMirror!.ReflectGrowthPercentPerStack = 0,
            o => o.Profiles["fire-test"].ReflectionMirror!.MaxHpCapGrowthPercentPerStack = -1,
            o => o.Profiles["fire-test"].ReflectionMirror!.ReflectGrowthPercentPerStack = 10,
            o => o.Profiles["fire-test"].ReflectionMirror!.AmplificationStatusCode = "mirror-budget",
            o => o.StatusEffects.Single(s => s.Code == "mirror-amplification").IsDispellable = true
        })
        {
            var options = MirrorLv3Options(); fault(options);
            Assert.Throws<InvalidOperationException>(() => new MonsterCombatCatalog(Options.Create(options)));
        }
        var source = MirrorLv3Options(); var catalog = new MonsterCombatCatalog(Options.Create(source));
        var exported = catalog.ExportOptions(); source.Profiles["fire-test"].ReflectionMirror!.GrowthRounds = 0;
        exported.Profiles["fire-test"].ReflectionMirror!.ReflectGrowthPercentPerStack = 10;
        Assert.Equal(3, catalog.FindProfile("fire-test")!.ReflectionMirror!.GrowthRounds);
        Assert.Equal(.5m, catalog.FindProfile("fire-test")!.ReflectionMirror!.ReflectGrowthPercentPerStack);
    }

    private static MonsterCombatOptions MirrorLv3Options()
    {
        var options = MirrorTestOptions();
        var mirror = options.Profiles["fire-test"].ReflectionMirror!;
        mirror.InitialStacks = 4; mirror.GrowthRounds = 3;
        mirror.ReflectGrowthPercentPerStack = .5m; mirror.MaxHpCapGrowthPercentPerStack = 1m;
        mirror.AmplificationStatusCode = "mirror-amplification";
        var counter = options.StatusEffects.Single(s => s.Code == "mirror-test");
        counter.InitialStacks = counter.MaxStacks = 4;
        options.StatusEffects.Add(new()
        {
            Code = "mirror-amplification", Name = "Amplification", Description = "Amplification", EffectType = "None",
            IsPositive = true, IsDispellable = false, MaxStacks = 3, InitialStacks = 1,
            CounterKind = BattleStatusCounterKind.Stacks, Lifetime = BattleStatusLifetime.Rounds
        });
        return options;
    }
}
