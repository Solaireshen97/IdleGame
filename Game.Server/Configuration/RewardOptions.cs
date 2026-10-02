namespace Game.Server.Configuration;

public sealed class RewardOptions
{
    public const string SectionName = "Rewards";
    public bool GrantFirstHuntWeapon { get; set; }
    // Live source policy also applies to rewards captured by older rooms.
    public bool AllowConsumableDrops { get; set; }
    public bool UseLiveGoldRewards { get; set; }
    public CoopDropBonusOptions CoopDropBonus { get; set; } = new();
    public Dictionary<string, RewardBundleOptions> MonsterKills { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RewardBundleOptions> DungeonClears { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CoopDropBonusOptions
{
    // Zero defaults preserve old room snapshots and callers without an explicit policy.
    public decimal PercentPerAdditionalUser { get; set; }
    public decimal MaximumPercent { get; set; }
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
