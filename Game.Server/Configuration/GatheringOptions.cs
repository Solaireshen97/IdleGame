namespace Game.Server.Configuration;

public sealed class GatheringOptions
{
    public const string SectionName = "Gathering";
    public List<GatheringPointOptions> Points { get; set; } = [];
}

public sealed class GatheringPointOptions
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string RegionCode { get; set; } = string.Empty;
    public string MaterialCode { get; set; } = string.Empty;
    public bool IsRare { get; set; }
    public int OutputQuantity { get; set; } = 1;
    public int CycleSeconds { get; set; } = 20;
    public int MinimumCharacterLevel { get; set; } = 1;
    public int MinimumGatheringLevel { get; set; } = 1;
    public string UnlockKind { get; set; } = string.Empty;
    public string UnlockTargetCode { get; set; } = string.Empty;
    public int RequiredCount { get; set; } = 1;
}
