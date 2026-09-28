using Game.Server.Configuration;
using Game.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class RegionLootTests
{
    private static IConfiguration Configuration() => new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();

    [Theory]
    [InlineData("Fire")]
    [InlineData("Water")]
    [InlineData("Earth")]
    [InlineData("Wind")]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void EveryRegionalMonsterHasDistinctPrimaryAndSecondaryLootOfItsOwnElement(string element)
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
                    Assert.Empty(drops);
                    continue;
                }
                Assert.InRange(drops.Count, 2, 3);
                Assert.Equal(drops.Count, drops.Select(drop => drop.Code).Distinct().Count());
                Assert.All(drops, drop =>
                {
                    Assert.Equal(region.FeaturedElement, weapons.FindItem(drop.Code)!.Element);
                    Assert.Equal(1, drop.Quantity);
                    Assert.InRange(drop.ChancePercent, 1m, 10m);
                });
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
            var expected = index == 0 ? new[] { 3m, 1m } : index == 1 ? new[] { 6m, 2m } : new[] { 10m, 4m, 2m };
            Assert.Equal(expected, drops.Select(drop => drop.ChancePercent));
            Assert.True(huntFingerprints.Add(string.Join(";", drops.OrderBy(drop => drop.Code)
                .Select(drop => $"{drop.Code}:{drop.ChancePercent}"))));
            foreach (var drop in drops)
            {
                reachable.Add(drop.Code);
                Assert.Equal(new[] { 2, 1 }, weapons.FindItem(drop.Code)!.Skills.Select(skill => skill.Level));
            }
        }
        Assert.Equal(4, reachable.Count);
    }

    [Theory]
    [InlineData(0.02, 1)]
    [InlineData(0.005, 2)]
    [InlineData(0.04, 0)]
    public void MultipleWeaponDropsAreRolledIndependentlyAndPreviewMatchesTheTutorialTarget(double roll, int count)
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
        Assert.Equal(new[] { "t1-candle-staff", "t1-burning-blade-hatchet" }, preview.Select(drop => drop.Code));
        Assert.Equal(new[] { 3m, 1m }, preview.Select(drop => drop.ChancePercent));
        Assert.Equal(preview[0].Code, rewards.FirstHuntWeapon(hunt)!.Code);
        var drops = rewards.Roll(hunt, false, 1, 1, "test", 1, 1).Where(entry => entry.Kind == "Weapon").ToList();
        Assert.Equal(count, drops.Count);
        Assert.Equal(preview.Take(count).Select(drop => drop.Code), drops.Select(drop => drop.Code));
        Assert.All(drops, entry => Assert.Equal(entry.Code, RewardCatalog.DeserializeWeapon(entry)!.Code));
    }

    private sealed class FixedRandom(double roll) : Random
    {
        public override double NextDouble() => roll;
        public override long NextInt64(long maxValue) => 0;
    }
}
