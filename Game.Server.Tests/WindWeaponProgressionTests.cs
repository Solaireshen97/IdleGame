using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class WindWeaponProgressionTests
{
    [Fact]
    public void WindHuntDropsUnlocksAndHealthDependentGridMatchTransitionDesign()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();
        T Bind<T>(string section) where T : class, new() => configuration.GetSection(section).Get<T>()!;
        var catalog = new WeaponCatalog(Options.Create(Bind<WeaponOptions>(WeaponOptions.SectionName)));
        var rewards = new RewardCatalog(Options.Create(Bind<RewardOptions>(RewardOptions.SectionName)),
            new ConsumableCatalog(Options.Create(Bind<ConsumableOptions>(ConsumableOptions.SectionName))), catalog,
            new MaterialCatalog(Options.Create(Bind<MaterialOptions>(MaterialOptions.SectionName))),
            new SoulImprintCatalog(Options.Create(Bind<SoulImprintOptions>(SoulImprintOptions.SectionName))));

        const string a = "t1-feather-short-staff", b = "t1-hide-wrapped-club", c = "t1-sinew-shortbow";
        foreach (var item in new[]
        {
            (Code: a, Attack: 120, Hp: 105, First: "weapon-attack", Second: "weapon-stamina"),
            (Code: b, Attack: 105, Hp: 135, First: "weapon-health", Second: "weapon-resilience-small"),
            (Code: c, Attack: 115, Hp: 110, First: "weapon-critical-large", Second: "weapon-double-small")
        })
        {
            var template = catalog.FindItem(item.Code)!;
            Assert.Equal((ElementType.Wind, item.Attack, item.Hp, 2),
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

        Assert.Equal(a, rewards.FirstHuntWeapon("mulgore-plainstrider-chick")!.Code);
        Assert.Null(rewards.FirstHuntWeapon("mulgore-bristleback-forager"));
        Assert.Null(rewards.FirstHuntWeapon("mulgore-venture-lumberjack"));
        Assert.Equal(new[] { (b, 5m), (a, 2m) }, rewards.GetDropPreview("mulgore-bristleback-forager", false)
            .Where(drop => drop.Kind == "Weapon").Select(drop => (drop.Code, drop.ChancePercent)));
        Assert.Equal(new[] { (c, 5m), (b, 2m), (a, 1m) }, rewards.GetDropPreview("mulgore-venture-lumberjack", false)
            .Where(drop => drop.Kind == "Weapon").Select(drop => (drop.Code, drop.ChancePercent)));
        var bowPreview = rewards.GetDropPreview("mulgore-venture-lumberjack", false)
            .First(drop => drop.Code == c).Weapon!;
        Assert.Equal(new[] { ("暴击大", 0), ("二连击小", 3) },
            bowPreview.Skills.Select(skill => (skill.Name, skill.UnlockQualityRank)));

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
        Assert.Equal((1140, 1155, 2194), (character.Attack, character.MaxHp, character.Hp));
        Assert.Equal(100m, bonuses.AttackPercent);
        Assert.Equal(90m, bonuses.HealthPercent);
        Assert.Equal(80m, character.WeaponStaminaPercent);
        Assert.Equal(60m, character.WeaponCriticalChancePercent);
        Assert.Equal(37.5m, character.WeaponDoubleAttackChancePercent);
        Assert.Equal(15m, WeaponCombatRules.WeaponReductionPercent(character));

        decimal ExpectedBasicAttack()
        {
            var factors = new DamageFactors(AttackPercent: WeaponCombatRules.AttackBonusPercent(character, 0),
                HealthPercent: WeaponCombatRules.HealthDamagePercent(character.Hp,
                    TalentRules.EffectiveMaxHp(character), character.WeaponStaminaPercent, character.WeaponEnmityPercent));
            var normal = DamageCalculator.Calculate(character.Attack, 0, factors: factors);
            var critical = DamageCalculator.Calculate(character.Attack, 0,
                factors: factors with { CriticalPercent = 50 });
            return (normal * .4m + critical * .6m) *
                (1m + character.WeaponDoubleAttackChancePercent / 100m);
        }

        Assert.InRange(ExpectedBasicAttack(), 7335m, 7337m);
        character.Hp = 1920;
        Assert.InRange(ExpectedBasicAttack(), 5704m, 5708m);
        character.Hp = 1645;
        Assert.InRange(ExpectedBasicAttack(), 4074m, 4077m);
    }
}
