using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class LightWeaponProgressionTests
{
    [Fact]
    public void LightHuntDropsUnlocksAndFullGridMatchTransitionDesign()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        T Bind<T>(string section) where T : class, new() => configuration.GetSection(section).Get<T>()!;
        var catalog = new WeaponCatalog(Options.Create(Bind<WeaponOptions>(WeaponOptions.SectionName)));
        var rewards = new RewardCatalog(Options.Create(Bind<RewardOptions>(RewardOptions.SectionName)),
            new ConsumableCatalog(Options.Create(Bind<ConsumableOptions>(ConsumableOptions.SectionName))), catalog,
            new MaterialCatalog(Options.Create(Bind<MaterialOptions>(MaterialOptions.SectionName))),
            new SoulImprintCatalog(Options.Create(Bind<SoulImprintOptions>(SoulImprintOptions.SectionName))));

        const string a = "t1-sentry-old-sword", b = "t1-dusty-prayer-mace", c = "t1-copper-ring-ritual-staff";
        foreach (var item in new[]
        {
            (Code: a, Attack: 110, Hp: 115, First: "weapon-attack", Second: "weapon-resilience"),
            (Code: b, Attack: 115, Hp: 105, First: "weapon-might", Second: "weapon-echo-small"),
            (Code: c, Attack: 110, Hp: 100, First: "weapon-critical", Second: "weapon-double")
        })
        {
            var template = catalog.FindItem(item.Code)!;
            Assert.Equal((ElementType.Light, item.Attack, item.Hp, 2),
                (template.Element, template.Attack, template.MaxHp, template.Revision));
            Assert.Equal(new[] { (item.First, 1, 0), (item.Second, 1, 3) },
                template.Skills.Select(skill => (skill.Code, skill.Level, skill.UnlockQualityRank)));
            var snapshot = catalog.CreateRewardSnapshot(item.Code);
            Assert.Equal((item.Attack, item.Hp, 2, 0),
                (snapshot.Attack, snapshot.MaxHp, snapshot.TemplateRevision, snapshot.QualityRank));
            var weapon = catalog.MaterializeReward(snapshot, 1);
            Assert.Equal(item.First, Assert.Single(weapon.Skills).SkillCode);
            weapon.QualityRank = 3;
            catalog.UnlockSkillsForQuality(weapon);
            Assert.Equal(new[] { item.First, item.Second },
                weapon.Skills.OrderBy(skill => skill.SlotIndex).Select(skill => skill.SkillCode));
            Assert.All(weapon.Skills, skill => Assert.Equal(1, skill.Level));
        }

        Assert.Equal(a, rewards.FirstHuntWeapon("eversong-golden-lynx")!.Code);
        Assert.Null(rewards.FirstHuntWeapon("eversong-feral-treant"));
        Assert.Null(rewards.FirstHuntWeapon("eversong-runestone-sentinel"));
        Assert.Equal(new[] { (b, 5m), (a, 2m) }, rewards.GetDropPreview("eversong-feral-treant", false)
            .Where(drop => drop.Kind == "Weapon").Select(drop => (drop.Code, drop.ChancePercent)));
        Assert.Equal(new[] { (c, 5m), (b, 2m), (a, 1m) }, rewards.GetDropPreview("eversong-runestone-sentinel", false)
            .Where(drop => drop.Kind == "Weapon").Select(drop => (drop.Code, drop.ChancePercent)));
        var staffPreview = rewards.GetDropPreview("eversong-runestone-sentinel", false)
            .First(drop => drop.Code == c).Weapon!;
        Assert.Equal(new[] { ("暴击中", 0), ("二连击中", 3) },
            staffPreview.Skills.Select(skill => (skill.Name, skill.UnlockQualityRank)));

        var equipped = new List<CharacterWeapon>();
        foreach (var (code, count) in new[] { (a, 4), (b, 3), (c, 3) })
            for (var n = 0; n < count; n++)
            {
                var weapon = catalog.MaterializeReward(catalog.CreateRewardSnapshot(code), 1);
                weapon.QualityRank = 3;
                catalog.UnlockSkillsForQuality(weapon);
                foreach (var skill in weapon.Skills)
                {
                    skill.EnhancementLevel = 9;
                    skill.Level = 10;
                }
                weapon.EquippedSlotIndex = equipped.Count + 1;
                equipped.Add(weapon);
            }
        var character = new Character
        {
            Attack = equipped.Sum(weapon => weapon.Attack),
            MaxHp = equipped.Sum(weapon => weapon.MaxHp)
        };
        var bonuses = catalog.ApplyBonuses(character, equipped);
        BattleConsumableBonusCalculator.ApplyCombatEffects(character, bonuses);
        character.Hp = TalentRules.EffectiveMaxHp(character);
        Assert.Equal((1115, 1075, 1720), (character.Attack, character.MaxHp, character.Hp));
        Assert.Equal(145m, bonuses.AttackPercent);
        Assert.Equal(60m, bonuses.HealthPercent);
        Assert.Equal(45m, character.WeaponCriticalChancePercent);
        Assert.Equal(52.5m, character.WeaponDoubleAttackChancePercent);
        Assert.Equal(30m, character.WeaponNormalEchoPercent);
        Assert.Equal(40m, WeaponCombatRules.WeaponReductionPercent(character));

        var factors = new DamageFactors(AttackPercent: WeaponCombatRules.AttackBonusPercent(character, 0));
        var normal = DamageCalculator.Calculate(character.Attack, 0, factors: factors);
        var critical = DamageCalculator.Calculate(character.Attack, 0,
            factors: factors with { CriticalPercent = 50 });
        var normalWithEcho = normal + WeaponCombatRules.EchoDamage(normal, character.WeaponNormalEchoPercent);
        var criticalWithEcho = critical + WeaponCombatRules.EchoDamage(critical, character.WeaponNormalEchoPercent);
        var expectedBasicAttack = (normalWithEcho * .55m + criticalWithEcho * .45m) *
            (1m + character.WeaponDoubleAttackChancePercent / 100m);
        Assert.InRange(expectedBasicAttack, 6600m, 6670m);
    }
}
