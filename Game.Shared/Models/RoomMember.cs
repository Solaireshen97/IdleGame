namespace Game.Shared.Models;

public class RoomMember
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public int PlayerId { get; set; }
    public int CharacterId { get; set; }
    public bool IsOwner { get; set; }
}
