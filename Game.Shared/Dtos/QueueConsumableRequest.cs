namespace Game.Shared.Dtos;

public class QueueConsumableRequest
{
    public int RoomId { get; set; }
    public int CharacterId { get; set; }
    public int? ConsumableSlotIndex { get; set; }
    public bool IsQueued { get; set; } = true;
    public int? ExpectedRoundNumber { get; set; }
    public int? ExpectedRunSequence { get; set; }
}
