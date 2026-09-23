namespace Game.Shared.Models;

public sealed class GatheringTask
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int CharacterId { get; set; }
    public string PointCode { get; set; } = string.Empty;
    public bool IsRare { get; set; }
    public string MaterialCode { get; set; } = string.Empty;
    public int CycleSeconds { get; set; }
    public int OutputQuantity { get; set; }
    public int ExtraYieldChancePercent { get; set; }
    public int RareBonusChancePercent { get; set; }
    public string? BonusMaterialCode { get; set; }
    public int BonusQuantity { get; set; }
    public int ExtraYieldQuantity { get; set; }
    public string Status { get; set; } = "Running";
    public DateTime StartedAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public DateTime NextCycleAtUtc { get; set; }
    public DateTime? StoppedAtUtc { get; set; }
    public int CompletedCycles { get; set; }
    public int TotalQuantity { get; set; }
    public int Version { get; set; }
}
