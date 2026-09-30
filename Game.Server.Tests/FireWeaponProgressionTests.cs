using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class FireWeaponProgressionTests
{
    [Fact]
    public void FireTransitionTemplatesSnapshotsAndVisibleHuntDropsMatchDesign()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        T Bind<T>(string section) where T : class, new() => configuration.GetSection(section).Get<T>()!;
        var catalog = new WeaponCatalog(Options.Create(Bind<WeaponOptions>(WeaponOptions.SectionName)));
        var rewards = new RewardCatalog(Options.Create(Bind<RewardOptions>(RewardOptions.SectionName)),
            new ConsumableCatalog(Options.Create(Bind<ConsumableOptions>(ConsumableOptions.SectionName))), catalog,
            new MaterialCatalog(Options.Create(Bind<MaterialOptions>(MaterialOptions.SectionName))),
            new SoulImprintCatalog(Options.Create(Bind<SoulImprintOptions>(SoulImprintOptions.SectionName))));
        const string a = "t1-candle-staff", b = "t1-burning-blade-hatchet", c = "t1-soot-iron-hammer";
        foreach (var item in new[]
        {
            (Code: a, Attack: 125, Hp: 95, First: "weapon-attack", Second: "weapon-critical"),
            (Code: b, Attack: 120, Hp: 110, First: "weapon-might", Second: "weapon-enmity"),
            (Code: c, Attack: 110, Hp: 125, First: "weapon-double-large", Second: "weapon-health-small")
        })
        {
            var template = catalog.FindItem(item.Code)!;
            Assert.Equal((ElementType.Fire, item.Attack, item.Hp, 2),
                (template.Element, template.Attack, template.MaxHp, template.Revision));
            Assert.Equal(new[] { (item.First, 1, 0), (item.Second, 1, 3) },
                template.Skills.Select(skill => (skill.Code, skill.Level, skill.UnlockQualityRank)));
            var snapshot = catalog.CreateRewardSnapshot(item.Code);
            Assert.Equal((item.Attack, item.Hp, 2, 0),
                (snapshot.Attack, snapshot.MaxHp, snapshot.TemplateRevision, snapshot.QualityRank));
            Assert.Equal((item.First, 1), (Assert.Single(snapshot.Skills).Code, snapshot.Skills[0].Level));
            var awarded = catalog.MaterializeReward(snapshot, 1);
            Assert.Equal(item.First, Assert.Single(awarded.Skills).SkillCode);
            Assert.Equal(0, awarded.QualityRank);
            awarded.QualityRank = 3;
            catalog.UnlockSkillsForQuality(awarded);
            Assert.Equal(new[] { item.First, item.Second },
                awarded.Skills.OrderBy(skill => skill.SlotIndex).Select(skill => skill.SkillCode));
            Assert.All(awarded.Skills, skill => Assert.Equal(1, skill.Level));
        }

        Assert.Equal(a, rewards.FirstHuntWeapon("durotar-valley-boar")!.Code);
        Assert.Null(rewards.FirstHuntWeapon("durotar-dust-raptor"));
        Assert.Null(rewards.FirstHuntWeapon("durotar-burning-cultist"));
        Assert.Equal(new[] { (b, 5m), (a, 2m) }, rewards.GetDropPreview("durotar-dust-raptor", false)
            .Where(drop => drop.Kind == "Weapon").Select(drop => (drop.Code, drop.ChancePercent)));
        Assert.Equal(new[] { (c, 5m), (b, 2m), (a, 1m) }, rewards.GetDropPreview("durotar-burning-cultist", false)
            .Where(drop => drop.Kind == "Weapon").Select(drop => (drop.Code, drop.ChancePercent)));
        Assert.Equal(new[] { 3m, 1m }, rewards.GetDropPreview("durotar-red-scorpion", false)
            .Where(drop => drop.Kind == "Weapon").Select(drop => drop.ChancePercent));
    }
}
