namespace Game.Server.Configuration;

public sealed class DungeonDepthOptions
{
    public const string SectionName = "DungeonDepths";
    public Dictionary<string, DungeonDepthDefinitionOptions> Dungeons { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DungeonDepthDefinitionOptions
{
    public int Revision { get; set; } = 1;
    public int Stage { get; set; } = 1;
    public bool UsesPlaceholderBalance { get; set; } = true;
    public string PrerequisiteDungeonCode { get; set; } = string.Empty;
    public int MaximumDepth { get; set; } = 10;
    public decimal GrowthPercent { get; set; } = 10m;
    public decimal GoldBonusPercent { get; set; } = 10m;
    public decimal KillExtraRollChancePercent { get; set; } = 10m;
    public decimal ClearExtraRollChancePercent { get; set; } = 10m;
    public string ChallengeFragmentCode { get; set; } = string.Empty;
    public decimal ChallengeFragmentChancePercent { get; set; } = 10m;
    public int ChallengeFragmentQuantity { get; set; } = 1;
    public int ChallengeStartDepth { get; set; } = 5;
}
