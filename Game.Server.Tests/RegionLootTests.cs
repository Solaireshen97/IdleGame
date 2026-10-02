using Game.Server.Configuration;
using Game.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class RegionLootTests
{
    private static IConfiguration Configuration() => new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(
        TestRepository.File("Game.Server", "appsettings.json"))).Build();

    [Theory]
    [InlineData("Fire")]
    [InlineData("Water")]
    [InlineData("Earth")]
    [InlineData("Wind")]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void RegionalLootMatchesHuntEntryAndDeepDungeonRules(string element)
    {
        var config = Configuration();
        var rewards = config.GetSection(RewardOptions.SectionName).Get<RewardOptions>()!;
        var encounters = config.GetSection(DungeonEncounterOptions.SectionName).Get<DungeonEncounterOptions>()!;
        var weapons = T1WeaponEffectTests.ProductionCatalog();
        var world = WorldCatalog.LoadDefault();
        var region = world.Regions.Single(region => region.FeaturedElement.ToString() == element);
        var challenges = world.Dungeons.Where(dungeon => dungeon.IsVisible && dungeon.RegionCode == region.Code).ToList();
        Assert.Equal(5, challenges.Count);
        // Check the whole region, including overlaps between hunts and dungeon waves.
        var fingerprints = new Dictionary<string, string>();

        foreach (var dungeon in challenges)
        {
            var monsters = encounters.Dungeons[dungeon.Code].SelectMany(wave => wave.Monsters).ToList();
            foreach (var monster in monsters)
            {
                var profile = string.IsNullOrWhiteSpace(monster.RewardProfileCode) ? dungeon.Code : monster.RewardProfileCode;
                var drops = rewards.MonsterKills[profile].Drops.Where(drop => drop.Kind == "Weapon").ToList();
                if (dungeon.DungeonKind == "Dungeon" && dungeon.MinimumLevel < 10)
                {
                    if (monster.IsBoss) Assert.Empty(drops);
                    else
                    {
                        var hunt = challenges.Single(hunt => hunt.DungeonKind == "Hunt" && hunt.MonsterName == monster.Name);
                        var primary = rewards.MonsterKills[hunt.Code].Drops.First(drop => drop.Kind == "Weapon");
                        var entryDrop = Assert.Single(drops);
                        Assert.Equal(primary.Code, entryDrop.Code);
                        Assert.Equal(15m, entryDrop.ChancePercent);
                        Assert.Equal(region.FeaturedElement, weapons.FindItem(entryDrop.Code)!.Element);
                    }
                    continue;
                }
                if (dungeon.DungeonKind == "Dungeon" && dungeon.MinimumLevel == 10)
                {
                    Assert.Equal(2, drops.Count);
                    Assert.Equal(2, drops.Select(drop => drop.Code).Distinct().Count());
                    Assert.All(drops, drop =>
                    {
                        Assert.StartsWith("t1-deep-", drop.Code);
                        Assert.Equal(region.FeaturedElement, weapons.FindItem(drop.Code)!.Element);
                        Assert.Equal(1, drop.Quantity);
                        Assert.Equal(monster.IsBoss ? 5m : 1m, drop.ChancePercent);
                    });
                    continue;
                }
                Assert.InRange(drops.Count, dungeon.DungeonKind == "Hunt" && dungeon.RecommendedLevel == 1 ? 1 : 2, 3);
                Assert.Equal(drops.Count, drops.Select(drop => drop.Code).Distinct().Count());
                Assert.All(drops, drop =>
                {
                    Assert.Equal(region.FeaturedElement, weapons.FindItem(drop.Code)!.Element);
                    Assert.Equal(1, drop.Quantity);
                    Assert.InRange(drop.ChancePercent, 1m, 10m);
                });
                if (drops.Count > 1)
                    Assert.True(drops[0].ChancePercent > drops.Skip(1).Max(drop => drop.ChancePercent));
                if (dungeon.DungeonKind == "Elite")
                    Assert.Equal(new[] { 3m, 2m }, drops.Select(drop => drop.ChancePercent));
                if (dungeon.Code == region.FeaturedDungeonCode && monster.IsBoss)
                    Assert.Equal(new[] { 5m, 2m }, drops.Select(drop => drop.ChancePercent));
                var fingerprint = string.Join(";", drops.OrderBy(drop => drop.Code)
                    .Select(drop => $"{drop.Code}:{drop.ChancePercent}"));
                if (fingerprints.TryGetValue(fingerprint, out var otherName)) Assert.Equal(monster.Name, otherName);
                fingerprints[fingerprint] = monster.Name;
            }
        }

        var hunts = challenges.Where(dungeon => dungeon.DungeonKind == "Hunt").OrderBy(dungeon => dungeon.SortOrder).ToList();
        Assert.Equal(3, hunts.Count);
        var huntFingerprints = new HashSet<string>();
        var reachable = new HashSet<string>();
        for (var index = 0; index < hunts.Count; index++)
        {
            var drops = rewards.MonsterKills[hunts[index].Code].Drops.Where(drop => drop.Kind == "Weapon").ToList();
            var expected = index == 0 ? new[] { 5m } : index == 1
                ? new[] { 5m, 2m } : new[] { 5m, 2m, 1m };
            Assert.Equal(expected, drops.Select(drop => drop.ChancePercent));
            Assert.True(huntFingerprints.Add(string.Join(";", drops.OrderBy(drop => drop.Code)
                .Select(drop => $"{drop.Code}:{drop.ChancePercent}"))));
            foreach (var drop in drops)
            {
                reachable.Add(drop.Code);
                Assert.All(weapons.FindItem(drop.Code)!.Skills, skill => Assert.Equal(1, skill.Level));
            }
        }
        Assert.InRange(reachable.Count, 3, 4);
    }

    [Theory]
    [InlineData(0.02)]
    [InlineData(0.005)]
    [InlineData(0.04)]
    public void FirstHuntWeaponDropAndPreviewMatchTheTutorialTarget(double roll)
    {
        var config = Configuration();
        IOptions<T> Bind<T>(string section) where T : class, new() => Options.Create(config.GetSection(section).Get<T>()!);
        var weapons = new WeaponCatalog(Bind<WeaponOptions>(WeaponOptions.SectionName));
        var rewards = new RewardCatalog(Bind<RewardOptions>(RewardOptions.SectionName),
            new ConsumableCatalog(Bind<ConsumableOptions>(ConsumableOptions.SectionName)), weapons,
            new MaterialCatalog(Bind<MaterialOptions>(MaterialOptions.SectionName)),
            new SoulImprintCatalog(Bind<SoulImprintOptions>(SoulImprintOptions.SectionName)), new FixedRandom(roll));
        const string hunt = "durotar-valley-boar";
        var preview = rewards.GetDropPreview(hunt, false).Where(drop => drop.Kind == "Weapon").ToList();
        Assert.Equal(new[] { "t1-candle-staff" }, preview.Select(drop => drop.Code));
        Assert.Equal(new[] { 5m }, preview.Select(drop => drop.ChancePercent));
        Assert.Equal(preview[0].Code, rewards.FirstHuntWeapon(hunt)!.Code);
        Assert.Null(rewards.FirstHuntWeapon("durotar-red-scorpion"));
        Assert.Equal(0, rewards.FirstHuntWeapon(hunt)!.QualityRank);
        var drops = rewards.Roll(hunt, false, 1, 1, "test", 1, 1).Where(entry => entry.Kind == "Weapon").ToList();
        Assert.Single(drops);
        Assert.Equal(preview.Select(drop => drop.Code), drops.Select(drop => drop.Code));
        Assert.All(drops, entry =>
        {
            var snapshot = RewardCatalog.DeserializeWeapon(entry)!;
            Assert.Equal(entry.Code, snapshot.Code);
            Assert.Equal(0, snapshot.QualityRank);
        });
    }

    [Fact]
    public void DeepDungeonKillWeaponRewardsAlwaysStartAtNormalQuality()
    {
        var config = Configuration();
        IOptions<T> Bind<T>(string section) where T : class, new() => Options.Create(config.GetSection(section).Get<T>()!);
        var weapons = new WeaponCatalog(Bind<WeaponOptions>(WeaponOptions.SectionName));
        var rewards = new RewardCatalog(Bind<RewardOptions>(RewardOptions.SectionName),
            new ConsumableCatalog(Bind<ConsumableOptions>(ConsumableOptions.SectionName)), weapons,
            new MaterialCatalog(Bind<MaterialOptions>(MaterialOptions.SectionName)),
            new SoulImprintCatalog(Bind<SoulImprintOptions>(SoulImprintOptions.SectionName)), new FixedRandom(0));

        var drops = rewards.Roll("kobold-mine-depths-boss", false, 1, 1, "monster:3:1", 1, 1)
            .Where(entry => entry.Kind == "Weapon").ToList();
        Assert.Equal(2, drops.Count);
        Assert.All(drops, entry => Assert.Equal(0, RewardCatalog.DeserializeWeapon(entry)!.QualityRank));
        Assert.DoesNotContain(rewards.Roll("kobold-mine-depths", true, 1, 1, "clear", 1, 1),
            entry => entry.Kind == "Weapon");
    }

    private sealed class FixedRandom(double roll) : Random
    {
        public override double NextDouble() => roll;
    }
}
