using Game.Server.Configuration;
using Microsoft.Extensions.Options;
namespace Game.Server.Services;
public sealed class PlantingCatalog
{
    public PlantingCatalog(IOptions<PlantingOptions> options)
    {
        if (options.Value.PlotCount != 4) throw new InvalidOperationException("Planting requires four plots.");
        Plants = options.Value.Plants.AsReadOnly();
        if (Plants.Select(p => p.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Plants.Count ||
            Plants.Select(p => p.SeedCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Plants.Count ||
            Plants.Any(p => string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.UnlockTargetCode) || p.UnlockKind is not ("MonsterKill" or "DungeonClear") || p.MinimumCharacterLevel < 1 || p.GrowthSeconds > 604800 || string.IsNullOrWhiteSpace(p.Code) || string.IsNullOrWhiteSpace(p.SeedCode) || string.IsNullOrWhiteSpace(p.MaterialCode) || p.GrowthSeconds <= 0 || p.HarvestQuantity <= 0 || p.RequiredCount <= 0 || (p.IsRare ? p.SeedPrice != 0 : p.SeedPrice <= 0) || p.DropChancePercent is < 0 or > 100))
            throw new InvalidOperationException("Invalid planting configuration.");
    }
    public IReadOnlyList<PlantOptions> Plants { get; }
    public PlantOptions? FindPlant(string? code) => Plants.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));
    public PlantOptions? FindSeed(string? code) => Plants.FirstOrDefault(p => string.Equals(p.SeedCode, code, StringComparison.OrdinalIgnoreCase));
}


