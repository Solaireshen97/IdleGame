namespace Game.Shared.Dtos;

public class JoinRoomRequest
{
    public int? CharacterId { get; set; }
    public Game.Shared.Dtos.Formations.LoadoutSelection? LoadoutSelection { get; set; }
    public string? RequestId { get; set; }
    public int SlotIndex { get; set; }
}
