namespace Game.Shared.Models;

public sealed class RewardRun
{
    public int RoomId { get; set; }
    public int Sequence { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTime? SettledAtUtc { get; set; }
}

public sealed class RewardEvent
{
    public int RoomId { get; set; }
    public int Sequence { get; set; }
    public string EventKey { get; set; } = string.Empty;
}

public sealed class RewardEntry
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public int Sequence { get; set; }
    public string EventKey { get; set; } = string.Empty;
    public int UserId { get; set; }
    public int CharacterId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public string? WeaponSnapshotJson { get; set; }
}
