using Game.Shared.Enums;

namespace Game.Shared.Models;

public class Room
{
    public int Id { get; set; }
    public int PlayerId { get; set; }
    public int CharacterId { get; set; }
    public int MonsterId { get; set; }
    public RoomStatus Status { get; set; }
}
