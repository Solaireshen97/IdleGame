using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class WaterWeaponProgressionTests
{
    [Fact]
    public void WaterHuntTemplatesDropsSnapshotsAndFullBuildMatchProgression()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();
        T Bind<T>(string section) where T : class, new() => configuration.GetSection(section).Get<T>()!;
        var catalog = new WeaponCatalog(Options.Create(Bind<WeaponOptions>(WeaponOptions.SectionName)));
        var rewards = new RewardCatalog(Options.Create(Bind<RewardOptions>(RewardOptions.SectionName)),
            new ConsumableCatalog(Options.Create(Bind<ConsumableOptions>(ConsumableOptions.SectionName))), catalog,
            new MaterialCatalog(Options.Create(Bind<MaterialOptions>(MaterialOptions.SectionName))),
            new SoulImprintCatalog(Options.Create(Bind<SoulImprintOptions>(SoulImprintOptions.SectionName))));
        const string a = "t1-ice-tusk-mallet", b = "t1-fishbone-knife", c = "t1-frostmane-hunting-spear";
        var expected = new[]
        {
            (Code: a, Attack: 115, Hp: 110, Revision: 2, First: "weapon-attack", Second: "weapon-double"),
            (Code: b, Attack: 110, Hp: 125, Revision: 2, First: "weapon-might", Second: "weapon-critical-small"),
            (Code: c, Attack: 105, Hp: 135, Revision: 2, First: "weapon-stamina-small", Second: "weapon-health-small")
        };
        foreach (var item in expected)
        {
            var template = catalog.FindItem(item.Code)!;
            Assert.Equal((item.Attack, item.Hp, item.Revision),
                (template.Attack, template.MaxHp, template.Revision));
            Assert.Equal(new[] { (item.First, 1, 0), (item.Second, 1, 3) },
                template.Skills.Select(skill => (skill.Code, skill.Level, skill.UnlockQualityRank)));
            var snapshot = catalog.CreateRewardSnapshot(item.Code);
            Assert.Equal((item.Attack, item.Hp, item.Revision, 0),
                (snapshot.Attack, snapshot.MaxHp, snapshot.TemplateRevision, snapshot.QualityRank));
            Assert.Equal((item.First, 1), (Assert.Single(snapshot.Skills).Code, snapshot.Skills[0].Level));
            var awarded = catalog.MaterializeReward(snapshot, 1);
            Assert.Equal((item.Code, item.Attack, item.Hp, item.Revision, 0),
                (awarded.WeaponCode, awarded.Attack, awarded.MaxHp, awarded.TemplateRevision, awarded.QualityRank));
            Assert.Equal(item.First, Assert.Single(awarded.Skills).SkillCode);
        }

        Assert.Equal(a, rewards.FirstHuntWeapon("dun-morogh-snow-hare")!.Code);
        Assert.Null(rewards.FirstHuntWeapon("dun-morogh-rockjaw-digger"));
        Assert.Null(rewards.FirstHuntWeapon("dun-morogh-ice-shell-boar"));
        Assert.Equal(new[] { (b, 5m), (a, 2m) }, rewards.GetDropPreview("dun-morogh-rockjaw-digger", false)
            .Where(drop => drop.Kind == "Weapon").Select(drop => (drop.Code, drop.ChancePercent)));
        Assert.Equal(new[] { (c, 5m), (b, 2m), (a, 1m) }, rewards.GetDropPreview("dun-morogh-ice-shell-boar", false)
            .Where(drop => drop.Kind == "Weapon").Select(drop => (drop.Code, drop.ChancePercent)));

        var equipped = new List<CharacterWeapon>();
        foreach (var (code, count) in new[] { (a, 4), (b, 3), (c, 3) })
            for (var n = 0; n < count; n++)
            {
                var weapon = catalog.MaterializeReward(catalog.CreateRewardSnapshot(code), 1);
                weapon.QualityRank = 3;
                catalog.UnlockSkillsForQuality(weapon);
                Assert.Equal(2, weapon.Skills.Count);
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
        character.Hp = TalentRules.EffectiveMaxHp(character);
        Assert.Equal(1105, character.Attack);
        Assert.Equal(1220, character.MaxHp);
        Assert.Equal(2684, character.Hp);
        Assert.Equal(145m, bonuses.AttackPercent);
        Assert.Equal(120m, bonuses.HealthPercent);
        Assert.Equal(30m, bonuses.CriticalChancePercent);
        Assert.Equal(70m, bonuses.Percent(WeaponSkillEffectType.DoubleAttackChancePercent));
        Assert.Equal(30m, WeaponCombatRules.HealthDamagePercent(character.Hp, character.Hp,
            character.WeaponStaminaPercent, character.WeaponEnmityPercent));

        var factors = new DamageFactors(AttackPercent: WeaponCombatRules.AttackBonusPercent(character, 0),
            HealthPercent: WeaponCombatRules.HealthDamagePercent(character.Hp, character.Hp,
                character.WeaponStaminaPercent, character.WeaponEnmityPercent));
        var normal = DamageCalculator.Calculate(character.Attack, 0, factors: factors);
        var critical = DamageCalculator.Calculate(character.Attack, 0,
            factors: factors with { CriticalPercent = 50 });
        var expectedNormalAttack = (normal * (1m - character.WeaponCriticalChancePercent / 100m) +
            critical * character.WeaponCriticalChancePercent / 100m) *
            (1m + character.WeaponDoubleAttackChancePercent / 100m);
        Assert.InRange(expectedNormalAttack, 6800m, 7000m);
    }
}
