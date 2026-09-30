using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class EarthWeaponProgressionTests
{
    [Fact]
    public void EarthHuntDropsUnlocksAndFullGridMatchTransitionDesign()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        T Bind<T>(string section) where T : class, new() => configuration.GetSection(section).Get<T>()!;
        var catalog = new WeaponCatalog(Options.Create(Bind<WeaponOptions>(WeaponOptions.SectionName)));
        var rewards = new RewardCatalog(Options.Create(Bind<RewardOptions>(RewardOptions.SectionName)),
            new ConsumableCatalog(Options.Create(Bind<ConsumableOptions>(ConsumableOptions.SectionName))), catalog,
            new MaterialCatalog(Options.Create(Bind<MaterialOptions>(MaterialOptions.SectionName))),
            new SoulImprintCatalog(Options.Create(Bind<SoulImprintOptions>(SoulImprintOptions.SectionName))));

        const string a = "t1-stone-edge-hatchet", b = "t1-boar-tusk-club", c = "t1-chipped-mining-pick";
        foreach (var item in new[]
        {
            (Code: a, Attack: 100, Hp: 120, First: "weapon-attack", Second: "weapon-health"),
            (Code: b, Attack: 110, Hp: 135, First: "weapon-attack-small", Second: "weapon-resolve-small"),
            (Code: c, Attack: 120, Hp: 130, First: "weapon-echo-large", Second: "weapon-growth")
        })
        {
            var template = catalog.FindItem(item.Code)!;
            Assert.Equal((ElementType.Earth, item.Attack, item.Hp, 2),
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

        Assert.Equal(a, rewards.FirstHuntWeapon("northshire-wolves")!.Code);
        Assert.Null(rewards.FirstHuntWeapon("stone-tusk-boars"));
        Assert.Null(rewards.FirstHuntWeapon("kobold-miners"));
        Assert.Equal(new[] { (b, 5m), (a, 2m) }, rewards.GetDropPreview("stone-tusk-boars", false)
            .Where(drop => drop.Kind == "Weapon").Select(drop => (drop.Code, drop.ChancePercent)));
        Assert.Equal(new[] { (c, 5m), (b, 2m), (a, 1m) }, rewards.GetDropPreview("kobold-miners", false)
            .Where(drop => drop.Kind == "Weapon").Select(drop => (drop.Code, drop.ChancePercent)));
        var pickPreview = rewards.GetDropPreview("kobold-miners", false).First(drop => drop.Code == c).Weapon!;
        Assert.Equal(new[] { ("追击大", 0), ("精进中", 3) },
            pickPreview.Skills.Select(skill => (skill.Name, skill.UnlockQualityRank)));

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
        Assert.Equal((1090, 1275, 2805), (character.Attack, character.MaxHp, character.Hp));
        Assert.Equal(145m, bonuses.AttackPercent);
        Assert.Equal(120m, bonuses.HealthPercent);
        Assert.Equal(60m, character.WeaponNormalEchoPercent);
        Assert.Equal(12m, character.CombatWeaponRampAttackPerRoundPercent);
        Assert.Equal(37.5m, character.CombatWeaponLowHpReductionPercent);
        Assert.Equal(0m, WeaponCombatRules.WeaponReductionPercent(character));
        Assert.Equal(205m, WeaponCombatRules.AttackBonusPercent(character, 4));
        Assert.Equal(265m, WeaponCombatRules.AttackBonusPercent(character, 9));
        var roundFive = DamageCalculator.Calculate(character.Attack, 0,
            factors: new DamageFactors(AttackPercent: WeaponCombatRules.AttackBonusPercent(character, 4)));
        var roundTen = DamageCalculator.Calculate(character.Attack, 0,
            factors: new DamageFactors(AttackPercent: WeaponCombatRules.AttackBonusPercent(character, 9)));
        Assert.Equal(5318, roundFive + WeaponCombatRules.EchoDamage(roundFive, character.WeaponNormalEchoPercent));
        Assert.Equal(6364, roundTen + WeaponCombatRules.EchoDamage(roundTen, character.WeaponNormalEchoPercent));
    }
}
