using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class ProfessionMechanicCatalogTests
{
    [Fact]
    public void ProductionConfigurationValidatesAllRequiredMechanics()
    {
        var (skills, statuses) = Production();
        ProfessionMechanicCatalog.Default.Validate(skills, statuses);
    }

    [Fact]
    public void DefaultsPreserveCurrentMechanicNumbersAndLevelSelection()
    {
        var catalog = new ProfessionMechanicCatalog();
        Assert.Equal(30, catalog.Knight.CounterAttackPowerPercent);
        Assert.Equal(20, catalog.Rogue.DamageBonusPerChargePercent);
        Assert.Equal(35, catalog.Rogue.ExecuteBelowHpPercent);
        Assert.Equal(30, catalog.Rogue.ExecuteActualDamagePercent);
        Assert.Equal(35, catalog.Hunter.MarkedPrecisionAttackBonusPercent.ForLevel(2));
        Assert.Equal(20, catalog.Hunter.MarkedCoordinatedPower.ForLevel(3));
        Assert.Equal(3, catalog.Mage.EchoRequiredStacks);
        Assert.Equal(50, catalog.Mage.DomainEchoAttackPowerPercent);
        Assert.Equal(15, catalog.Acolyte.DamageEnhancementPercent);
        Assert.Equal(20, catalog.Acolyte.HealingEnhancementPercent);
        Assert.Equal(3, catalog.Acolyte.RevelationChargesByLevel.ForLevel(3));
    }

    [Fact]
    public void HunterDynamicVariantNeverChoosesSharedStatus()
    {
        var (_, statuses) = Production();
        Assert.Equal("hunter-vulnerability-10", ProfessionMechanicCatalog.Default.HunterVulnerabilityStatus(statuses, 2).Code);
        Assert.Equal("hunter-coordinated-16", ProfessionMechanicCatalog.Default.HunterCoordinatedStatus(statuses, 2).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrWrongPoweredDynamicVariantFailsClearly(bool wrongPower)
    {
        var options = new MonsterCombatOptions();
        if (wrongPower) options.StatusEffects.Add(new()
        {
            Code = "hunter-vulnerability-10", Name = "Wrong variant", Description = "Invalid strength",
            EffectType = "None", Mechanic = BattleStatusMechanic.HunterVulnerability, MechanicPower = 9
        });
        var statuses = new BattleStatusCatalog(Options.Create(options));
        var failure = Assert.Throws<InvalidOperationException>(() =>
            ProfessionMechanicCatalog.Default.HunterVulnerabilityStatus(statuses, 2));
        Assert.Contains("hunter-vulnerability-10", failure.Message);
        Assert.Contains("HunterVulnerability", failure.Message);
    }

    [Fact]
    public void SkillDescriptionUsesTypedMechanicValuesInsteadOfLegacyDescription()
    {
        var (skills, statuses) = Production();
        var catalog = new ProfessionMechanicCatalog(rogue: new(DamageBonusPerChargePercent: 27,
            ExecuteBelowHpPercent: 41, ExecuteActualDamagePercent: 33));
        var skill = skills.FindDefinition("rogue-execution-slash")! with { Description = "obsolete 999%" };
        var information = new SkillInformationService(statuses, catalog);
        var description = information.Description(skill);
        Assert.Contains("27%", description);
        Assert.Contains("41%", description);
        Assert.Contains("33%", description);
        Assert.DoesNotContain("999%", description);
        Assert.Equal(skill.Effects.Length, information.Effects(skill).Count);
        Assert.Equal(skill.Effects[0].AttackPowerPercent, information.Effects(skill)[0].AttackPowerPercent);
    }

    [Fact]
    public void ArbitraryVariantCodeResolvesByUniqueMechanicAndPower()
    {
        var statuses = new BattleStatusCatalog(Options.Create(new MonsterCombatOptions
        {
            StatusEffects = [new() { Code = "custom-native", Name = "Custom", Description = "Custom variant",
                EffectType = "None", Mechanic = BattleStatusMechanic.HunterVulnerability, MechanicPower = 10 }]
        }));
        Assert.Equal("custom-native", ProfessionMechanicCatalog.Default.HunterVulnerabilityStatus(statuses, 2).Code);
        Assert.Null(ProfessionMechanicCatalog.Default.TryHunterCoordinatedStatus(statuses, 2));
    }

    [Fact]
    public void InvalidThresholdAndWrongCounterLifetimeAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => new ProfessionMechanicCatalog(mage: new(new(15, 20, 25), EchoRequiredStacks: 0)));
        var statuses = new BattleStatusCatalog(Options.Create(new MonsterCombatOptions
        {
            StatusEffects = [new() { Code = "custom-disruption", Name = "Custom", Description = "Wrong lifetime",
                EffectType = "None", Mechanic = BattleStatusMechanic.NextDamageSkillReduction, MechanicPower = 15,
                Lifetime = BattleStatusLifetime.Rounds, CounterKind = BattleStatusCounterKind.Charges }]
        }));
        Assert.Throws<InvalidOperationException>(() => ProfessionMechanicCatalog.Default.MageDisruptionStatus(statuses, 0));
    }

    [Fact]
    public void AmbiguousCustomVariantsDoNotDependOnConfigurationOrder()
    {
        var statuses = new BattleStatusCatalog(Options.Create(new MonsterCombatOptions
        {
            StatusEffects = [
                new() { Code = "first", Name = "First", Description = "Variant", EffectType = "None",
                    Mechanic = BattleStatusMechanic.HunterVulnerability, MechanicPower = 10 },
                new() { Code = "second", Name = "Second", Description = "Variant", EffectType = "None",
                    Mechanic = BattleStatusMechanic.HunterVulnerability, MechanicPower = 10 }
            ]
        }));
        Assert.Null(ProfessionMechanicCatalog.Default.TryHunterVulnerabilityStatus(statuses, 2));
        Assert.Throws<InvalidOperationException>(() => ProfessionMechanicCatalog.Default.HunterVulnerabilityStatus(statuses, 2));
    }

    [Fact]
    public void PurifySelfCleanseThresholdChangesSnapshotAndDescriptionTogether()
    {
        var (skills, statuses) = Production();
        var skill = skills.FindDefinition("acolyte-purify")! with { Level = 2 };
        var catalog = new ProfessionMechanicCatalog(acolyte: new(new(1, 2, 3), PurifySelfCleanseMinLevel: 3));
        var snapshot = new SkillBattleSnapshot(1, [], 100, 100, true, false, false, false, []);
        Assert.True(SkillBattleSnapshotFactory.ForSkill(snapshot, skill).AdditionalSelfCleanse);
        Assert.False(SkillBattleSnapshotFactory.ForSkill(snapshot, skill, catalog).AdditionalSelfCleanse);
        var information = new SkillInformationService(statuses, catalog);
        Assert.DoesNotContain("另为自身净化", information.Description(skill));
        Assert.Contains("另为自身净化", information.Description(skill with { Level = 3 }));
        Assert.Contains("神启强化时", information.Description(skill with { Level = 1 }));
    }

    [Fact]
    public void GeneratedDescriptionRetainsTypedConditionalDamageRules()
    {
        var (skills, statuses) = Production();
        var skill = skills.FindDefinition("rogue-execution-slash")! with
        {
            ConditionalDamageBonusPercent = 17, RequiredTargetStatusCode = "rogue-poison", TargetHpBelowPercent = 42
        };
        var description = new SkillInformationService(statuses).Description(skill);
        Assert.Contains("17%", description);
        Assert.Contains("42%", description);
        Assert.Contains("不高于 42%", description);
        Assert.Contains(statuses.Find("rogue-poison")!.Name, description);
    }

    [Fact]
    public void StatusDescriptionAndSharedSkillCompatibilityRemainConsistent()
    {
        var (skills, statuses) = Production();
        var catalog = new ProfessionMechanicCatalog(acolyte: new(new(1, 2, 3), HealingEnhancementPercent: 26));
        var description = new ProfessionMechanicDescription(catalog);
        Assert.Contains("26%", description.Status(statuses.Find("acolyte-next-heal")!));
        var information = new SkillInformationService(statuses, catalog);
        var shared = skills.FindDefinition("acolyte-heal")! with { IsShared = true, Description = "Shared compatibility" };
        Assert.Equal("Shared compatibility", information.Description(shared));
        Assert.Equal(shared.Effects.Length, information.Effects(shared).Count);
        Assert.Contains("26%", information.Description(shared with { IsShared = false }));
    }

    private static (SkillCatalog Skills, BattleStatusCatalog Statuses) Production()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        var monsters = new MonsterCombatCatalog(Options.Create(configuration.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!));
        return (new SkillCatalog(Options.Create(configuration.GetSection(SkillOptions.SectionName).Get<SkillOptions>()!), monsters), monsters.Statuses);
    }
}

public partial class BattleServiceTests
{
    [Fact]
    public async Task CustomMageEchoPowerChangesActualBattleDamageAndDisplayedMechanic()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterDefense: 0);
        test.Character.ProfessionCode = "mage";
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        var configuration = new ConfigurationBuilder().AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        var monsters = new MonsterCombatCatalog(Options.Create(configuration.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!));
        var skills = new SkillCatalog(Options.Create(configuration.GetSection(SkillOptions.SectionName).Get<SkillOptions>()!), monsters);
        var statuses = new BattleStatusService(test.Db, monsters.Statuses);
        var mechanics = new ProfessionMechanicCatalog(mage: new(new(15, 20, 25), EchoAttackPowerPercent: 45));
        await statuses.SetCounterAsync(test.Room, "Character", test.Character.Id, "mage-disorder", 3,
            boundTargetType: "Monster", boundTargetId: test.Monster.Id);
        var battle = new BattleExecutionContext(test.Room, test.Monster, await UnifiedPartyAsync(test.Db),
            new Dictionary<int, ElementType>(), new Dictionary<int, OperationPotionBonuses>(), []);
        await MageMechanics.RoundStartAsync(battle, UnifiedExecutor(statuses, skills), mechanics);
        Assert.Equal(955, test.Monster.Hp);
        Assert.Equal(0, await MageMechanics.DisorderStacksAsync(statuses, test.Room, test.Character.Id, test.Monster.Id));
        Assert.Contains("45%", new SkillInformationService(monsters.Statuses, mechanics).Description(skills.FindDefinition("mage-arcane-bolt")!));
    }

    [Fact]
    public async Task LiveStatusSnapshotUsesTheSameCustomMechanicNumbersAsSkillInformation()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var configuration = new ConfigurationBuilder().AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        var monsters = new MonsterCombatCatalog(Options.Create(configuration.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!));
        var mechanics = new ProfessionMechanicCatalog(acolyte: new(new(1, 2, 3), HealingEnhancementPercent: 26));
        var statuses = new BattleStatusService(test.Db, monsters.Statuses, mechanics: mechanics);
        await statuses.SetCounterAsync(test.Room, "Character", test.Character.Id, "acolyte-next-heal", 1);
        var snapshot = Assert.Single(await statuses.DescribeAsync(test.Room, "Character", test.Character.Id));
        Assert.Contains("26%", snapshot.Description);
        Assert.Equal(new ProfessionMechanicDescription(mechanics).Status(monsters.Statuses.Find("acolyte-next-heal")!), snapshot.Description);
        await statuses.ApplyAsync(test.Room, "Monster", test.Monster.Id, "mage-skill-disruption-15", 0,
            [], "Enemy", magnitudeSnapshot: 37);
        var dynamicSnapshot = Assert.Single(await statuses.DescribeAsync(test.Room, "Monster", test.Monster.Id));
        Assert.Contains("37%", dynamicSnapshot.Description);
        Assert.DoesNotContain("15%", dynamicSnapshot.Description);
        Assert.Contains("15%", new ProfessionMechanicDescription(mechanics).Status(monsters.Statuses.Find("mage-skill-disruption-15")!));
    }
}
