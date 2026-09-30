using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class DarkWeaponProgressionTests
{
    [Fact]
    public void DarkHuntDropsUnlocksAndHealthBandsMatchTransitionDesign()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        T Bind<T>(string section) where T : class, new() => configuration.GetSection(section).Get<T>()!;
        var catalog = new WeaponCatalog(Options.Create(Bind<WeaponOptions>(WeaponOptions.SectionName)));
        var rewards = new RewardCatalog(Options.Create(Bind<RewardOptions>(RewardOptions.SectionName)),
            new ConsumableCatalog(Options.Create(Bind<ConsumableOptions>(ConsumableOptions.SectionName))), catalog,
            new MaterialCatalog(Options.Create(Bind<MaterialOptions>(MaterialOptions.SectionName))),
            new SoulImprintCatalog(Options.Create(Bind<SoulImprintOptions>(SoulImprintOptions.SectionName))));

        const string a = "t1-dim-apprentice-staff", b = "t1-grave-thorn-staff", c = "t1-wood-hilt-ritual-dagger";
        foreach (var item in new[]
        {
            (Code: a, Attack: 130, Hp: 90, First: "weapon-attack", Second: "weapon-enmity"),
            (Code: b, Attack: 100, Hp: 130, First: "weapon-health", Second: "weapon-resolve-small"),
            (Code: c, Attack: 110, Hp: 105, First: "weapon-stamina", Second: "weapon-momentum")
        })
        {
            var template = catalog.FindItem(item.Code)!;
            Assert.Equal((ElementType.Dark, item.Attack, item.Hp, 2),
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

        Assert.Equal(a, rewards.FirstHuntWeapon("tirisfal-dusk-bat")!.Code);
        Assert.Null(rewards.FirstHuntWeapon("tirisfal-rotting-ghoul"));
        Assert.Null(rewards.FirstHuntWeapon("tirisfal-forsaken-acolyte"));
        Assert.Equal(new[] { (b, 5m), (a, 2m) }, rewards.GetDropPreview("tirisfal-rotting-ghoul", false)
            .Where(drop => drop.Kind == "Weapon").Select(drop => (drop.Code, drop.ChancePercent)));
        Assert.Equal(new[] { (c, 5m), (b, 2m), (a, 1m) }, rewards.GetDropPreview("tirisfal-forsaken-acolyte", false)
            .Where(drop => drop.Kind == "Weapon").Select(drop => (drop.Code, drop.ChancePercent)));
        var daggerPreview = rewards.GetDropPreview("tirisfal-forsaken-acolyte", false)
            .First(drop => drop.Code == c).Weapon!;
        Assert.Equal(new[] { ("强壮中", 0), ("连势", 3) },
            daggerPreview.Skills.Select(skill => (skill.Name, skill.UnlockQualityRank)));

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
        Assert.Equal((1150, 1065, 2023), (character.Attack, character.MaxHp, character.Hp));
        Assert.Equal(100m, bonuses.AttackPercent);
        Assert.Equal(90m, bonuses.HealthPercent);
        Assert.Equal(60m, character.WeaponStaminaPercent);
        Assert.Equal(200m, character.WeaponEnmityPercent);
        Assert.Equal(37.5m, character.WeaponDoubleAttackChancePercent);
        Assert.Equal(30m, character.WeaponNormalEchoPercent);
        Assert.Equal(37.5m, character.CombatWeaponLowHpReductionPercent);

        decimal ExpectedBasicAttack()
        {
            var factors = new DamageFactors(AttackPercent: WeaponCombatRules.AttackBonusPercent(character, 0),
                HealthPercent: WeaponCombatRules.HealthDamagePercent(character.Hp,
                    TalentRules.EffectiveMaxHp(character), character.WeaponStaminaPercent, character.WeaponEnmityPercent));
            var normal = DamageCalculator.Calculate(character.Attack, 0, factors: factors);
            return (normal + WeaponCombatRules.EchoDamage(normal, character.WeaponNormalEchoPercent)) *
                (1m + character.WeaponDoubleAttackChancePercent / 100m);
        }

        Assert.Equal(0m, WeaponCombatRules.WeaponReductionPercent(character));
        Assert.Equal(6578m, ExpectedBasicAttack());
        character.Hp = 1250;
        Assert.Equal(0m, WeaponCombatRules.HealthDamagePercent(character.Hp,
            TalentRules.EffectiveMaxHp(character), character.WeaponStaminaPercent, character.WeaponEnmityPercent));
        Assert.InRange(ExpectedBasicAttack(), 4110m, 4112m);
        character.Hp = 506;
        Assert.InRange(WeaponCombatRules.WeaponReductionPercent(character), 18.7m, 18.8m);
        Assert.InRange(ExpectedBasicAttack(), 8200m, 8240m);
    }
}
