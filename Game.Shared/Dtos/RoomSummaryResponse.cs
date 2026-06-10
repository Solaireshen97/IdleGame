using Game.Shared.Enums;

namespace Game.Shared.Dtos;

public class RoomSummaryResponse
{
    public int RoomId { get; set; }
    public string MonsterName { get; set; } = string.Empty;
    public int MonsterHp { get; set; }
    public int MonsterMaxHp { get; set; }
    public RoomStatus RoomStatus { get; set; }
}
