namespace Game.Shared.Dtos.Planting;
public sealed record PlantingOverviewResponse(int CharacterId, string CharacterName, DateTime ServerTimeUtc, List<GardenPlotDto> Plots, List<PlantDto> Plants);
public sealed record GardenPlotDto(int PlotIndex, int Version, string? PlantCode, string? PlantName, string? MaterialCode, int HarvestQuantity, DateTime? PlantedAtUtc, DateTime? MaturesAtUtc);
public sealed record PlantDto(string Code, string Name, string SeedCode, string MaterialCode, string RegionCode, bool IsRare, int GrowthSeconds, int HarvestQuantity, int SeedPrice, int SeedQuantity, bool IsUnlocked, string UnlockKind, string UnlockTargetCode, int RequiredCount, int Progress, string UnlockTargetName,
    int DropChancePercent = 0, int DropChancePerDepthPercent = 0, bool FirstClearGuaranteed = false);
public sealed record PlantGardenRequest(int CharacterId, string RequestId, string PlantCode, List<PlotVersionRequest> Plots);
public sealed record PlotVersionRequest(int PlotIndex, int ExpectedVersion);
public sealed record HarvestGardenRequest(int CharacterId, List<PlotVersionRequest> Plots);

