namespace Game.Shared.Dtos;

public class QueueConsumableRequest
{
    public int RoomId { get; set; }
    public int CharacterId { get; set; }
    public int? ConsumableSlotIndex { get; set; }
}
