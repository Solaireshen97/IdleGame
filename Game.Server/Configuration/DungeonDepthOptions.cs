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
    public List<int> CalibratedDepths { get; set; } = [];
    public bool UsesPlaceholderAt(int depth) => UsesPlaceholderBalance && !CalibratedDepths.Contains(depth);
    public string PrerequisiteDungeonCode { get; set; } = string.Empty;
    public int MaximumDepth { get; set; } = 10;
    public decimal GrowthPercent { get; set; } = 10m;
    // Missing fields preserve the shared growth rate in old frozen rooms.
    public decimal? ChallengeHpGrowthPercent { get; set; }
    public decimal? ChallengeAttackGrowthPercent { get; set; }
    public decimal GoldBonusPercent { get; set; } = 10m;
    public decimal KillExtraRollChancePercent { get; set; } = 10m;
    public decimal ClearExtraRollChancePercent { get; set; } = 10m;
    public string ChallengeFragmentCode { get; set; } = string.Empty;
    public decimal ChallengeFragmentChancePercent { get; set; } = 10m;
    public int ChallengeFragmentQuantity { get; set; } = 1;
    public int ChallengeStartDepth { get; set; } = 5;
    public Dictionary<int, int> ChallengeFragmentQuantities { get; set; } = [];
    public string ChallengeFirstClearItemCode { get; set; } = string.Empty;
    public Dictionary<int, int> ChallengeFirstClearQuantities { get; set; } = [];

    public int ChallengeFragmentsAt(int depth) => ChallengeFragmentQuantities.GetValueOrDefault(depth, ChallengeFragmentQuantity);
}
