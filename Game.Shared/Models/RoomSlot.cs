namespace Game.Shared.Models;

public class RoomSlot
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public int SlotIndex { get; set; }
    public int? CharacterId { get; set; }
    public int? UserId { get; set; }
    public bool IsMainControl { get; set; }
    public bool IsConfirmed { get; set; }
    public bool IsAutoEnabled { get; set; }
    public bool IsTemporaryAuto { get; set; }
    public int? PendingConsumableSlotIndex { get; set; }
}
