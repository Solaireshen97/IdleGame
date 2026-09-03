namespace Game.Shared.Dtos;

public class RoomSlotResponse
{
    public int SlotIndex { get; set; }
    public int? CharacterId { get; set; }
    public string? CharacterName { get; set; }
    public int? CharacterHp { get; set; }
    public int? CharacterMaxHp { get; set; }
    public bool IsOccupied { get; set; }
    public bool IsMainControl { get; set; }
    public bool IsCurrentUserCharacter { get; set; }
    public bool IsAlive { get; set; }
}
