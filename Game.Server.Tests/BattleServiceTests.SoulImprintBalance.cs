using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    private static async Task EquipBalancedSoulAsync(BattleTestContext test, Character character, string code,
        string? condition = null, int threshold = 70)
    {
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        test.Room.RoundNumber = 4;
        await test.EnableAutoForCharacterAsync(character);
        test.Db.CharacterSoulImprints.Add(new CharacterSoulImprint
        {
            CharacterId = character.Id, SoulImprintCode = code,
            EquippedSlotIndex = SoulImprintRules.SlotIndex, AutoUseEnabled = true,
            AutoConditionOverride = condition, AutoHpThresholdPercent = threshold
        });
        await test.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task EarthSoulImprintAmplifiesAllDamageAndKeepsStrongerHunterVulnerability()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        var (service, combat) = CreateProductionSoulBattleService(test);
        await EquipBalancedSoulAsync(test, test.Character, "deep-overseer-core");

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(error);
        Assert.Contains(result!.Events, fact => fact.SkillCode == "deep-overseer-core" && fact.Kind == BattleEventKind.Damage);
        Assert.Equal(105, await combat.Statuses.AmplifyDamageAsync(test.Room, test.Monster.Id, 100, BattleDamageScope.Direct));
        Assert.Equal(105, await combat.Statuses.AmplifyDamageAsync(test.Room, test.Monster.Id, 100, BattleDamageScope.Periodic));
        Assert.Equal(-20m, combat.Statuses.Catalog.Find("armor-break")!.ValuePerStack);

        await combat.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id, "hunter-vulnerability-8", 1, [], test.Monster.Name);
        Assert.Equal(108, await combat.Statuses.AmplifyDamageAsync(test.Room, test.Monster.Id, 100));
        var active = await combat.Statuses.GetActiveAsync(test.Room, "Monster", [test.Monster.Id]);
        Assert.Single(active, status => status.EffectCode == "hunter-vulnerability-8");
        Assert.DoesNotContain(active, status => status.EffectCode == "soul-earth-vulnerability");
    }

    [Fact]
    public async Task EarthSoulImprintWaitsForConfiguredMonsterHealthWithoutSpendingCooldown()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        var (service, combat) = CreateProductionSoulBattleService(test);
        await EquipBalancedSoulAsync(test, test.Character, "deep-overseer-core", "MonsterHpBelowThreshold", 50);
        await combat.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id, "hunter-vulnerability-8", 2, [], test.Monster.Name);
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(error);
        Assert.DoesNotContain(result!.Events, fact => fact.SkillCode == "deep-overseer-core");
        Assert.False(await test.Db.BattleSkillCooldowns.AnyAsync(c => c.SkillCode == SoulImprintRules.CooldownCode("deep-overseer-core")));
    }

    [Fact]
    public async Task TwoEarthSoulImprintsBothCastButDoNotStackVulnerability()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        var ally = await test.AddSlotAsync(2, "土印队友");
        var (service, combat) = CreateProductionSoulBattleService(test);
        await EquipBalancedSoulAsync(test, test.Character, "deep-overseer-core");
        await EquipBalancedSoulAsync(test, ally, "deep-overseer-core");

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(error);
        Assert.Equal(2, result!.Events.Count(fact => fact.SkillCode == "deep-overseer-core" && fact.Kind == BattleEventKind.Damage));
        Assert.Equal(2, await test.Db.BattleSkillCooldowns.CountAsync(c =>
            c.SkillCode == SoulImprintRules.CooldownCode("deep-overseer-core")));
        Assert.Equal(105, await combat.Statuses.AmplifyDamageAsync(test.Room, test.Monster.Id, 100));
    }

    [Fact]
    public async Task WindSoulImprintAutoUsesOneProfessionCooldownAndIgnoresOtherCooldowns()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        var (service, _) = CreateProductionSoulBattleService(test);
        await EquipBalancedSoulAsync(test, test.Character, "storm-matriarch-plume");
        foreach (var code in new[] { "sword-slash", "consumable:test", SoulImprintRules.CooldownCode("plague-widow-essence") })
            test.Db.BattleSkillCooldowns.Add(new BattleSkillCooldown
            {
                RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillCode = code, ReadyAtRound = 10
            });
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(error);
        Assert.Contains(result!.Events, fact => fact.SkillCode == "storm-matriarch-plume");
        Assert.Equal(9, (await test.Db.BattleSkillCooldowns.SingleAsync(c => c.SkillCode == "sword-slash")).ReadyAtRound);
        Assert.All(await test.Db.BattleSkillCooldowns.Where(c => c.SkillCode == "consumable:test" ||
            c.SkillCode == SoulImprintRules.CooldownCode("plague-widow-essence")).ToListAsync(),
            cooldown => Assert.Equal(10, cooldown.ReadyAtRound));
    }

    [Fact]
    public async Task WindSoulImprintMakesAProfessionSkillAvailableInTheSameRound()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        var (service, _) = CreateProductionSoulBattleService(test);
        await EquipBalancedSoulAsync(test, test.Character, "storm-matriarch-plume");
        await test.AddSkillAsync(test.Character, 1, "sword-slash", autoUse: true);
        foreach (var code in new[] { "sword-slash", "knight-rebuke" })
            test.Db.BattleSkillCooldowns.Add(new BattleSkillCooldown
            {
                RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillCode = code, ReadyAtRound = 5
            });
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(error);
        var wind = Assert.Single(result!.Events, fact => fact.SkillCode == "storm-matriarch-plume" && fact.Kind == BattleEventKind.Damage);
        var skill = Assert.Single(result.Events, fact => fact.SkillCode == "sword-slash" && fact.Kind == BattleEventKind.Damage);
        Assert.Equal(ElementType.Wind, wind.Element);
        Assert.True(wind.Sequence < skill.Sequence);
    }

    [Fact]
    public async Task LightSoulImprintHealsEveryoneButCleansesOnlyTheLowestHealthDebuffedAlly()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 80, monsterAttack: 1);
        var ally = await test.AddSlotAsync(2, "负伤队友", hp: 40);
        var (service, combat) = CreateProductionSoulBattleService(test);
        await EquipBalancedSoulAsync(test, test.Character, "dawn-core-prism");
        foreach (var character in new[] { test.Character, ally })
            await combat.ApplyStatusAsync(test.Room, "Character", character.Id, "poison", 3, [], character.Name);
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(error);
        var heals = result!.Events.Where(fact => fact.SkillCode == "dawn-core-prism" && fact.Kind == BattleEventKind.Heal).ToList();
        Assert.Equal(2, heals.Count);
        Assert.All(heals, heal => Assert.Equal(10, heal.ActualAmount));
        var cleanse = Assert.Single(result.Events, fact => fact.SkillCode == "dawn-core-prism" && fact.Kind == BattleEventKind.Cleanse);
        Assert.Equal(ally.Id, cleanse.Target.ActorId);
        Assert.True(await combat.Statuses.HasAsync(test.Room, "Character", test.Character.Id, "poison"));
        Assert.False(await combat.Statuses.HasAsync(test.Room, "Character", ally.Id, "poison"));
        Assert.Equal(14, (await test.Db.BattleSkillCooldowns.SingleAsync(c => c.SkillCode ==
            SoulImprintRules.CooldownCode("dawn-core-prism"))).ReadyAtRound);
    }

    [Fact]
    public async Task LightSoulImprintPrioritizesMechanicDebuffOnHealthierAllyOverOlderOrdinaryPoison()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 40, monsterAttack: 1);
        var ally = await test.AddSlotAsync(2, "霜缚队友", hp: 90);
        test.Monster.CombatProfileCode = "water-deep-lv4-boss";
        var (service, combat) = CreateProductionSoulBattleService(test);
        await EquipBalancedSoulAsync(test, test.Character, "dawn-core-prism");
        foreach (var character in new[] { test.Character, ally })
            await combat.ApplyStatusAsync(test.Room, "Character", character.Id, "poison", 3, [], character.Name);
        await combat.ApplyStatusAsync(test.Room, "Character", ally.Id, "water-deep-lv4-cold", 3, [], ally.Name);
        test.Db.MonsterIntents.Add(new MonsterIntent
        {
            RoomId = test.Room.Id, RunSequence = test.Room.RunSequence, RoundNumber = test.Room.RoundNumber,
            MonsterId = test.Monster.Id, ActionType = "BasicAttack", TargetType = "Front", TargetCharacterId = test.Character.Id
        });
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(error);
        var cleanse = Assert.Single(result!.Events, fact => fact.SkillCode == "dawn-core-prism" && fact.Kind == BattleEventKind.Cleanse);
        Assert.Equal(ally.Id, cleanse.Target.ActorId);
        Assert.Equal("water-deep-lv4-cold", cleanse.Status!.Code);
        Assert.True(await combat.Statuses.HasAsync(test.Room, "Character", test.Character.Id, "poison"));
        Assert.True(await combat.Statuses.HasAsync(test.Room, "Character", ally.Id, "poison"));
        Assert.False(await combat.Statuses.HasAsync(test.Room, "Character", ally.Id, "water-deep-lv4-cold"));
    }

    [Theory]
    [InlineData(80, 80, 80, true)]
    [InlineData(79, 100, 80, true)]
    [InlineData(60, 100, 60, true)]
    [InlineData(61, 100, 60, false)]
    [InlineData(81, 80, 79, false)]
    public async Task LightSoulImprintAutoUsesConfiguredAllyHealthThreshold(int ownerHp, int allyHp, int threshold, bool shouldCast)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: ownerHp, monsterAttack: 1);
        await test.AddSlotAsync(2, "队友", hp: allyHp);
        var (service, _) = CreateProductionSoulBattleService(test);
        await EquipBalancedSoulAsync(test, test.Character, "dawn-core-prism", "AllyHpBelowThreshold", threshold);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(error);
        Assert.Equal(shouldCast, result!.Events.Any(fact => fact.SkillCode == "dawn-core-prism" && fact.Kind == BattleEventKind.Heal));
    }
}
