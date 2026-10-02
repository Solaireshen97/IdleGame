namespace Game.Shared.Dtos;

public class AssignRoomSlotRequest
{
    public Game.Shared.Dtos.Formations.LoadoutSelection? LoadoutSelection { get; set; }
    public string? RequestId { get; set; }
    public int SlotIndex { get; set; }
    public int CharacterId { get; set; }
}
