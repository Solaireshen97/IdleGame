namespace Game.Server.Configuration;

public sealed class CombatDamageOptions
{
    public const string SectionName = "CombatDamage";
    // Explicit configuration enables variance; old room snapshots and unconfigured callers stay deterministic.
    public decimal VariancePercent { get; set; }
}
