using Game.Shared.Enums;

namespace Game.Shared.Dtos;

public class RoomDetailResponse
{
    public int RoomId { get; set; }
    public string MonsterName { get; set; } = string.Empty;
    public int MonsterHp { get; set; }
    public int MonsterMaxHp { get; set; }
    public RoomStatus RoomStatus { get; set; }
    public bool HasPlayer { get; set; }
    public string? PlayerName { get; set; }
    public string? CharacterName { get; set; }
    public int? CharacterHp { get; set; }
    public int? CharacterMaxHp { get; set; }
    public bool IsCurrentPlayerOwner { get; set; }
}
