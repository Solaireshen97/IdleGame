namespace Game.Server.Configuration;

public sealed class RewardOptions
{
    public const string SectionName = "Rewards";
    public Dictionary<string, RewardBundleOptions> MonsterKills { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RewardBundleOptions> DungeonClears { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RewardBundleOptions
{
    public int Gold { get; set; }
    public int Experience { get; set; }
    public List<RewardDropOptions> Drops { get; set; } = [];
}

public sealed class RewardDropOptions
{
    public string Kind { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public decimal ChancePercent { get; set; } = 100;
}
