using Game.Shared.Enums;

namespace Game.Shared.Dtos;

public class RoomStateResponse
{
    public int RoomId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public string CharacterName { get; set; } = string.Empty;
    public int CharacterHp { get; set; }
    public int CharacterMaxHp { get; set; }
    public string MonsterName { get; set; } = string.Empty;
    public int MonsterHp { get; set; }
    public int MonsterMaxHp { get; set; }
    public RoomStatus RoomStatus { get; set; }
}
