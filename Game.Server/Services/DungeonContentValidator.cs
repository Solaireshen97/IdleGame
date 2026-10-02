namespace Game.Server.Services;

public sealed class DungeonContentValidator(WorldCatalog world, WeaponCatalog weapons,
    DungeonEncounterCatalog encounters, RewardCatalog rewards, DungeonExchangeCatalog exchanges,
    DungeonDepthCatalog depths, MaterialCatalog materials, PartyScalingCatalog parties,
    WeaponBreakthroughCatalog breakthroughs)
{
    public void Validate()
    {
        world.ValidateContent(weapons, encounters, rewards, exchanges);
        var dungeons = world.Dungeons.ToDictionary(dungeon => dungeon.Code, StringComparer.OrdinalIgnoreCase);
        foreach (var code in encounters.DungeonCodes.Concat(depths.DungeonCodes).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!dungeons.ContainsKey(code))
                throw new InvalidOperationException($"Unknown world dungeon in content configuration: {code}");

        foreach (var dungeon in world.Dungeons)
        {
            if (depths.Find(dungeon.Code) is { } depth)
            {
                if (!string.IsNullOrWhiteSpace(depth.PrerequisiteDungeonCode) && !world.Dungeons.Any(candidate =>
                    candidate.Code == depth.PrerequisiteDungeonCode && candidate.RegionCode == dungeon.RegionCode &&
                    candidate.DungeonKind == "Dungeon" && candidate.IsVisible && depths.Find(candidate.Code) is null))
                    throw new InvalidOperationException($"Invalid ordinary dungeon prerequisite for {dungeon.Code}: {depth.PrerequisiteDungeonCode}");
                if (materials.FindItem(depth.ChallengeFragmentCode) is null)
                    throw new InvalidOperationException($"Unknown challenge fragment for {dungeon.Code}: {depth.ChallengeFragmentCode}");
                if (depth.ChallengeFirstClearQuantities.Count > 0 && materials.FindItem(depth.ChallengeFirstClearItemCode) is null)
                    throw new InvalidOperationException($"Unknown challenge first-clear item for {dungeon.Code}: {depth.ChallengeFirstClearItemCode}");
            }

            ValidateCombinedStats(dungeon, encounters, depths, parties);
        }
        foreach (var recipe in breakthroughs.Recipes)
            if (materials.FindItem(recipe.FragmentCode) is null || materials.FindItem(recipe.StoneCode) is null)
                throw new InvalidOperationException($"Unknown breakthrough materials for T{recipe.Tier}");
    }

    public static void ValidateCombinedStats(Game.Shared.Models.Dungeon dungeon, DungeonEncounterCatalog encounters,
        DungeonDepthCatalog depths, PartyScalingCatalog parties)
    {
        var maximumDepth = depths.Find(dungeon.Code)?.MaximumDepth ?? 1;
        var percent = parties.GetHpPercent(dungeon.PartyScalingProfileCode, dungeon.SlotCount);
        try
        {
            foreach (var monster in encounters.CreateMonsters(dungeon, maximumDepth))
                _ = checked((int)(((long)monster.BaseMaxHp * percent + 99) / 100));
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                $"Invalid combined dungeon stats: {dungeon.Code}; LV{maximumDepth} with {dungeon.SlotCount} characters exceeds the 32-bit HP range.", exception);
        }
    }
}
