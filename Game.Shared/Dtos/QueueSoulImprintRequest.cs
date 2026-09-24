namespace Game.Shared.Dtos;

public sealed class QueueSoulImprintRequest
{
    public int RoomId { get; set; }
    public int CharacterId { get; set; }
    public bool IsQueued { get; set; }
}
