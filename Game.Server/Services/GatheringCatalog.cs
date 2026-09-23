using Game.Server.Configuration;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class GatheringCatalog
{
    private readonly Dictionary<string, GatheringPointOptions> _points = new(StringComparer.OrdinalIgnoreCase);

    public GatheringCatalog(IOptions<GatheringOptions> options, WorldCatalog world, MaterialCatalog materials)
    {
        foreach (var point in options.Value.Points)
        {
            var dungeon = world.Dungeons.FirstOrDefault(item => item.Code == point.UnlockTargetCode &&
                item.RegionCode == point.RegionCode && item.IsVisible);
            if (string.IsNullOrWhiteSpace(point.Code) || string.IsNullOrWhiteSpace(point.Name) ||
                !_points.TryAdd(point.Code, point) ||
                !world.Regions.Any(region => region.Code == point.RegionCode) ||
                materials.FindItem(point.MaterialCode) is null ||
                point.CycleSeconds is < 1 or > 3600 || point.OutputQuantity <= 0 || point.RequiredCount <= 0 ||
                point.MinimumCharacterLevel <= 0 || point.MinimumGatheringLevel <= 0 || dungeon is null ||
                point.UnlockKind is not ("MonsterKill" or "DungeonClear") ||
                point.UnlockKind == "DungeonClear" && dungeon.DungeonKind != "Dungeon" ||
                point.IsRare && (point.UnlockKind != "MonsterKill" || dungeon.DungeonKind != "Elite"))
                throw new InvalidOperationException($"Invalid gathering point: {point.Code}");
        }
    }

    public IReadOnlyCollection<GatheringPointOptions> Points => _points.Values;

    public GatheringPointOptions? FindPoint(string? code) =>
        code is not null && _points.TryGetValue(code, out var point) ? point : null;
}
