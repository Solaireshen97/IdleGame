using System.Collections.Immutable;
using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    private const string DeepColdCode = "water-deep-lv2-cold", WarmCode = "water-deep-lv2-warm";

    [Fact]
    public async Task DeepColdHpTriggerWaitsForNextCompleteRoundAndNeverRepeatsAfterReload()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await ColdRigAsync(test);
        test.Room.RoundNumber = 60;
        test.Monster.Hp = 7001;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal(0, state.ActivationCount);
        for (var round = 61; round <= 64; round++)
        {
            test.Room.RoundNumber = round;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            Assert.True(state.IsActive);
            Assert.Equal(3, await rig.Statuses.StacksAsync(test.Room, "Character", test.Character.Id, DeepColdCode));
            Assert.Equal(-15m, await rig.Statuses.ModifierAsync(test.Room, "Character", test.Character.Id, "AttackPercent"));
            await ColdEndAsync(test, rig);
        }
        Assert.False(state.IsActive);
        Assert.Equal(1, state.ActivationCount);
        Assert.Equal(0, state.BreakCount);
        Assert.Equal(1, state.ExpiryCount);
        Assert.False(await rig.Statuses.HasAsync(test.Room, "Character", test.Character.Id, WarmCode));
        await test.Db.SaveChangesAsync();
        await using var db = test.CreateDbContext();
        var room = await db.Rooms.SingleAsync();
        var monster = await db.Monsters.SingleAsync();
        room.RoundNumber = 100;
        var phases = new MonsterPhaseService(db, rig.Catalog, new BattleStatusService(db, rig.Catalog.Statuses));
        await phases.BeginRoundAsync(room, monster, []);
        Assert.Equal(1, (await db.BattleMonsterPhaseStates.SingleAsync()).ActivationCount);
        Assert.False((await db.BattleMonsterPhaseStates.SingleAsync()).IsActive);
    }

    [Theory]
    [InlineData(BattleDamageOrigin.NormalAttack)]
    [InlineData(BattleDamageOrigin.Skill)]
    [InlineData(BattleDamageOrigin.Counter)]
    [InlineData(BattleDamageOrigin.Mechanic)]
    public async Task DeepColdCountsOnlyOneEffectiveEarthHitPerActorPerRound(BattleDamageOrigin origin)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        await test.AddSlotAsync(2, "第二土角色");
        var rig = await ColdRigAsync(test);
        test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        var source = BattleActor.ForCharacter(rig.Party[0]);
        await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Earth, 0, source.Id);
        await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Fire, 100, source.Id);
        await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Water, 100, source.Id);
        await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Earth, 100); // Derived echo has no actor credit.
        Assert.Equal(3, await rig.Statuses.StacksAsync(test.Room, "Character", source.Id, DeepColdCode));
        for (var hit = 0; hit < 3; hit++)
            await rig.Damage.CharacterDamageAsync(rig.Context, source, BattleSkillEffect.Damage(100), origin, false,
                damageElement: ElementType.Earth);
        Assert.All(rig.Party, p => Assert.Equal(2, test.Db.BattleStatusEffects.Local.Single(s =>
            s.TargetId == p.Character.Id && s.EffectCode == DeepColdCode).Stacks));
        await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Earth, 10, rig.Party[1].Character.Id);
        Assert.Equal(1, await rig.Statuses.StacksAsync(test.Room, "Character", source.Id, DeepColdCode));
        await ColdEndAsync(test, rig);
        test.Room.RoundNumber++;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        await rig.Damage.CharacterDamageAsync(rig.Context, source, BattleSkillEffect.Damage(100), origin, false,
            damageElement: ElementType.Earth);
        Assert.Equal(0, await rig.Statuses.StacksAsync(test.Room, "Character", source.Id, DeepColdCode));
        Assert.False(await rig.Statuses.HasAsync(test.Room, "Character", source.Id, WarmCode));
        Assert.Equal(2, Assert.Single(test.Db.BattleMonsterPhaseStates.Local).BreakCount);
    }

    [Fact]
    public async Task CleanseGrantsPersonalWarmForNextThreeFullRoundsAndSuppressesOrdinaryCold()
    {
        await using var test = await BattleTestContext.CreateAsync();
        await test.AddSlotAsync(2, "队友");
        var rig = await ColdRigAsync(test);
        test.Monster.Hp = 7000;
        await rig.Statuses.ApplyAsync(test.Room, "Character", test.Character.Id, "water-deep-lv1-chill-10", 2, [], "");
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        Assert.False(await rig.Statuses.HasAsync(test.Room, "Character", test.Character.Id, "water-deep-lv1-chill-10"));
        var cleanse = ColdCharacterSkill(BattleEffectKind.Cleanse);
        var cast = new BattleCastExecution(rig.Context, cleanse, BattleActor.ForCharacter(rig.Party[0]));
        var result = await rig.Effects.ExecuteAsync(cast);
        Assert.True(result.CleansingOccurred);
        Assert.Equal(0, await rig.Statuses.StacksAsync(test.Room, "Character", test.Character.Id, DeepColdCode));
        Assert.Equal(3, await rig.Statuses.StacksAsync(test.Room, "Character", rig.Party[1].Character.Id, DeepColdCode));
        Assert.False(await rig.Statuses.HasAsync(test.Room, "Character", test.Character.Id, WarmCode));
        Assert.Empty((await rig.Statuses.DescribeManyAsync(test.Room, "Character", [test.Character.Id]))[test.Character.Id]);
        Assert.False((await rig.Effects.ExecuteAsync(new(rig.Context, cleanse, cast.Source))).Applied);
        Assert.Equal(1, Assert.Single(test.Db.BattleMonsterPhaseStates.Local).BreakCount);
        for (var round = 1; round <= 4; round++)
        {
            await ColdEndAsync(test, rig);
            test.Room.RoundNumber = round;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            Assert.Equal(round <= 3 ? 15m : 0m,
                await rig.Statuses.ModifierAsync(test.Room, "Character", test.Character.Id, "DamageDealtPercent"));
            if (round <= 3)
            {
                var skill = rig.Catalog.ResolveSkill("water-deep-lv1-king-tide")!;
                var effect = skill.Effects.Single(e => e.Kind == BattleEffectKind.ApplyStatus);
                var outcome = await rig.Effects.ApplyStatusAsync(new(rig.Context, skill, rig.Context.Enemy), effect, cast.Source);
                Assert.False(outcome.Applied);
            }
        }
        Assert.Equal(0, await rig.Statuses.StacksAsync(test.Room, "Character", rig.Party[1].Character.Id, DeepColdCode));
        Assert.Equal(1, Assert.Single(test.Db.BattleMonsterPhaseStates.Local).BreakCount);
    }

    [Fact]
    public async Task LastWindowRoundEarthClearStillGetsThreeFullWarmRoundsAndDeathClearsBoundStatuses()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await ColdRigAsync(test);
        test.Monster.Hp = 7000;
        for (var round = 0; round <= 3; round++)
        {
            test.Room.RoundNumber = round;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            if (round > 0) await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Earth, 10, test.Character.Id);
            await ColdEndAsync(test, rig);
        }
        for (var round = 4; round <= 6; round++)
        {
            test.Room.RoundNumber = round;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            Assert.True(await rig.Statuses.HasAsync(test.Room, "Character", test.Character.Id, WarmCode));
            await ColdEndAsync(test, rig);
        }
        test.Room.RoundNumber = 7;
        Assert.False(await rig.Statuses.HasAsync(test.Room, "Character", test.Character.Id, WarmCode));
        await rig.Statuses.RemoveBoundToAsync(test.Room.Id, "Monster", test.Monster.Id);
        Assert.Empty(await rig.Statuses.GetActiveAsync(test.Room, "Character", [test.Character.Id]));
    }

    [Fact]
    public async Task WarmMultipliesFinalDirectDamageWithoutChangingHealingOrLegacyParry()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        var rig = await ColdRigAsync(test);
        var actor = BattleActor.ForCharacter(rig.Party[0]);
        var before = await rig.Damage.CharacterDamageAsync(rig.Context, actor, BattleSkillEffect.Damage(100),
            BattleDamageOrigin.Skill, false);
        var heal = ColdCharacterSkill(BattleEffectKind.Heal).Effects[0];
        var healing = BattleDamageService.CalculateHealing(actor, actor, heal);
        await rig.Statuses.ApplyAsync(test.Room, "Character", actor.Id, WarmCode, 2, [], "");
        var after = await rig.Damage.CharacterDamageAsync(rig.Context, actor, BattleSkillEffect.Damage(100),
            BattleDamageOrigin.Skill, false);
        Assert.Equal((int)decimal.Floor(before.CalculatedAmount * 1.15m), after.CalculatedAmount);
        Assert.Equal(healing, BattleDamageService.CalculateHealing(actor, actor, heal));
        var legacy = await rig.Damage.CharacterDamageAsync(rig.Context, actor, BattleSkillEffect.Damage(100),
            BattleDamageOrigin.LegacyParry, false);
        Assert.Equal(before.CalculatedAmount, legacy.CalculatedAmount);
    }

    [Fact]
    public void DeepColdDeclarationsValidateAndSurviveFrozenCatalogRoundTrip()
    {
        var options = WaterLv1Catalog().ExportOptions();
        foreach (var fault in new Action<DeepColdOptions>[]
        {
            c => c.TriggerHpPercent = 100, c => c.WindowRounds = 0, c => c.InitialStacks = 4,
            c => c.PendingStatusCode = c.WarmStatusCode, c => c.BasicColdStatusCodes = [WarmCode]
        })
        {
            var invalid = WaterLv1Catalog().ExportOptions();
            fault(invalid.Profiles["water-deep-lv2-boss"].DeepCold!);
            Assert.Throws<InvalidOperationException>(() => new MonsterCombatCatalog(Options.Create(invalid)));
        }
        var restored = new MonsterCombatCatalog(Options.Create(options));
        var profile = restored.FindProfile(restored.ResolveDepthProfile("water-deep-lv1-monster-5", 2))!;
        Assert.Equal(70, profile.DeepCold!.TriggerHpPercent);
        Assert.Equal(4, profile.DeepCold.WindowRounds);
        options.Profiles["water-deep-lv2-boss"].DeepCold!.TriggerHpPercent = 10;
        Assert.Equal(70, profile.DeepCold.TriggerHpPercent);
        Assert.Null(restored.FindProfile("water-deep-lv1-monster-5")!.DeepCold);
        Assert.All(restored.ExportOptions().StatusEffects.Where(s => s.IsHidden), s => Assert.Equal("None", s.EffectType));
    }

    private static CharacterSkillDefinition ColdCharacterSkill(BattleEffectKind kind) => new()
    {
        Code = "cold-test-" + kind, Name = "Test", Description = "Test", ProfessionCode = "acolyte",
        CooldownRounds = 0, InitialCooldownRounds = 0, Level = 1, IsShared = false,
        UnlockLevel = 1, Level2UnlockLevel = 1, Level3UnlockLevel = 1, AutoCondition = "Always",
        Effects = ImmutableArray.Create(new BattleSkillEffect(kind,
            new(BattleTargetSide.Self, BattleTargetSelection.Self, false, false, "Self"), Power: 20))
    };

    private static async Task<ColdRig> ColdRigAsync(BattleTestContext test, string profileCode = "water-deep-lv2-boss")
    {
        test.Monster.Hp = test.Monster.MaxHp = test.Monster.BaseMaxHp = 10000;
        test.Monster.Defense = 0;
        test.Monster.CombatProfileCode = profileCode;
        test.Monster.Element = ElementType.Water;
        var party = (await test.Db.RoomSlots.OrderBy(s => s.SlotIndex).ToListAsync())
            .Select(s => new BattleParticipant(s, test.Db.Characters.Local.Single(c => c.Id == s.CharacterId))).ToList();
        var catalog = WaterLv1Catalog();
        var statuses = new BattleStatusService(test.Db, catalog.Statuses);
        var phases = new MonsterPhaseService(test.Db, catalog, statuses);
        var guards = new BattleGuardService(statuses);
        var damage = new BattleDamageService(statuses, guards, new Random(7213), phases: phases);
        var effects = new BattleEffectExecutor(new SkillCatalog(Options.Create(new SkillOptions())), statuses, guards, damage);
        return new(catalog, statuses, phases, damage, effects, party,
            new(test.Room, test.Monster, party, new Dictionary<int, ElementType>(), new Dictionary<int, OperationPotionBonuses>(), []));
    }

    private static async Task ColdEndAsync(BattleTestContext test, ColdRig rig)
    {
        await rig.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, []);
        await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
    }

    private sealed record ColdRig(MonsterCombatCatalog Catalog, BattleStatusService Statuses,
        MonsterPhaseService Phases, BattleDamageService Damage, BattleEffectExecutor Effects,
        List<BattleParticipant> Party, BattleExecutionContext Context);
}
