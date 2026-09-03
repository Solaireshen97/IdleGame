using Game.Shared.Enums;

namespace Game.Shared.Models;

public class Room
{
    public int Id { get; set; }
    public int MonsterId { get; set; }
    public int OwnerUserId { get; set; }
    public int SlotCount { get; set; }
    public RoomStatus Status { get; set; }
    public DateTime? NextRoundAvailableAtUtc { get; set; }
    public DateTime? BattleEndedAtUtc { get; set; }
    public int Version { get; set; }
}
