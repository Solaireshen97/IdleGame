namespace Game.Server.Configuration;
public sealed class PlantingOptions
{
    public const string SectionName = "Planting";
    public int PlotCount { get; set; } = 4;
    public List<PlantOptions> Plants { get; set; } = [];
}
public sealed class PlantOptions
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string SeedCode { get; set; } = "";
    public string MaterialCode { get; set; } = "";
    public string RegionCode { get; set; } = "";
    public bool IsRare { get; set; }
    public int GrowthSeconds { get; set; }
    public int HarvestQuantity { get; set; }
    public int SeedPrice { get; set; }
    public string UnlockKind { get; set; } = "MonsterKill";
    public string UnlockTargetCode { get; set; } = "";
    public List<string> AlternativeUnlockTargetCodes { get; set; } = [];
    public int RequiredCount { get; set; } = 1;
    public int MinimumCharacterLevel { get; set; } = 1;
    public int DropChancePercent { get; set; } = 20;
    public int DropChancePerDepthPercent { get; set; }
    public bool FirstClearGuaranteed { get; set; }
}
