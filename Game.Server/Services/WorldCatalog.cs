using System.Text.Json;
using System.Text.Json.Serialization;
using Game.Server.Configuration;
using Game.Shared.Models;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class WorldCatalog
{
    public IReadOnlyList<RegionOptions> Regions { get; }
    public IReadOnlyList<Dungeon> Dungeons { get; }

    public WorldCatalog(IOptions<WorldOptions> options)
    {
        Regions = options.Value.Regions;
        Dungeons = options.Value.Dungeons;
        if (Regions.Count == 0 || Regions.Select(region => region.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Regions.Count ||
            Dungeons.Select(dungeon => dungeon.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Dungeons.Count)
            throw new InvalidOperationException("World must have unique region and dungeon codes.");
        foreach (var region in Regions)
        {
            if (string.IsNullOrWhiteSpace(region.Code) || string.IsNullOrWhiteSpace(region.Name) ||
                string.IsNullOrWhiteSpace(region.Description) || !Enum.IsDefined(region.FeaturedElement) ||
                region.MinimumLevel < 1 || region.MaximumLevel < region.MinimumLevel ||
                string.IsNullOrWhiteSpace(region.FeaturedWeaponCode) ||
                !Dungeons.Any(dungeon => dungeon.Code == region.FeaturedDungeonCode &&
                    dungeon.RegionCode == region.Code && dungeon.DungeonKind == "Dungeon" && dungeon.IsVisible))
                throw new InvalidOperationException($"Invalid region: {region.Code}");
        }
        foreach (var dungeon in Dungeons)
        {
            var region = Regions.SingleOrDefault(region => region.Code == dungeon.RegionCode);
            if (string.IsNullOrWhiteSpace(dungeon.Code) || string.IsNullOrWhiteSpace(dungeon.Name) || dungeon.Id != 0 ||
                dungeon.MinimumLevel < 1 || dungeon.RecommendedLevel < dungeon.MinimumLevel ||
                dungeon.MonsterMaxHp <= 0 || dungeon.MonsterAttack < 0 || dungeon.MonsterDefense < 0 ||
                dungeon.SlotCount != 5 || !Enum.IsDefined(dungeon.MonsterElement) ||
                dungeon.DungeonKind is not ("Hunt" or "Elite" or "Dungeon" or "Legacy") ||
                dungeon.IsVisible && (region is null || dungeon.MinimumLevel < region.MinimumLevel ||
                    dungeon.RecommendedLevel > region.MaximumLevel))
                throw new InvalidOperationException($"Invalid world dungeon: {dungeon.Code}");
            if (region is not null) dungeon.RegionName = region.Name;
        }
    }

    public void ValidateContent(WeaponCatalog weapons, DungeonEncounterCatalog encounters,
        RewardCatalog rewards, DungeonExchangeCatalog exchanges)
    {
        foreach (var dungeon in Dungeons.Where(dungeon => dungeon.IsVisible))
        {
            if (!encounters.HasDefinition(dungeon.Code) || !rewards.HasRewardProfile(dungeon.Code, true))
                throw new InvalidOperationException($"Missing encounter or clear rewards: {dungeon.Code}");
        }
        foreach (var region in Regions)
        {
            var weapon = weapons.FindItem(region.FeaturedWeaponCode);
            var dungeon = Dungeons.Single(dungeon => dungeon.Code == region.FeaturedDungeonCode);
            var boss = encounters.CreateMonsters(dungeon).Last();
            if (weapon is null || weapon.Element != region.FeaturedElement ||
                !boss.IsBoss || boss.Element != region.FeaturedElement)
                throw new InvalidOperationException($"Invalid regional boss or weapon: {region.Code}");
        }
        foreach (var offer in exchanges.Offers)
        {
            if (!Dungeons.Any(dungeon => dungeon.Code == offer.DungeonCode && dungeon.Name == offer.DungeonName && dungeon.DungeonKind == "Dungeon"))
                throw new InvalidOperationException($"Unknown exchange dungeon: {offer.DungeonCode}");
        }
    }

    // Used by database maintenance and tests without a host configuration container.
    public static WorldCatalog LoadDefault()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "world.json")));
        var options = document.RootElement.GetProperty(WorldOptions.SectionName).Deserialize<WorldOptions>(
            new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } })!;
        return new WorldCatalog(Options.Create(options));
    }
}
