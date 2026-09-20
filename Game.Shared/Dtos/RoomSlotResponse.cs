namespace Game.Shared.Dtos;

public class RoomSlotResponse
{
    public int SlotIndex { get; set; }
    public int? CharacterId { get; set; }
    public int? PendingConsumableSlotIndex { get; set; }
    public List<RoomConsumableSlotResponse> Consumables { get; set; } = [];
    public string? CharacterName { get; set; }
    public int? CharacterHp { get; set; }
    public int? CharacterMaxHp { get; set; }
    public int? CharacterLevel { get; set; }
    public int? CharacterExperience { get; set; }
    public int? ExperienceToNextLevel { get; set; }
    public int? TalentPoints { get; set; }
    public bool IsOccupied { get; set; }
    public bool IsMainControl { get; set; }
    public bool IsCurrentUserCharacter { get; set; }
    public bool IsAlive { get; set; }
    public bool IsConfirmed { get; set; }
    public string? PlayerName { get; set; }
    public bool IsAutoEnabled { get; set; }
    public bool IsTemporaryAuto { get; set; }
    public bool IsAutoUnlockedForCurrentUser { get; set; }
    public bool CanConfigureAuto { get; set; }
}

public class RoomConsumableSlotResponse
{
    public int SlotIndex { get; set; }
    public string? ItemCode { get; set; }
    public string? ItemName { get; set; }
    public int HealAmount { get; set; }
    public int Quantity { get; set; }
    public int CooldownRoundsRemaining { get; set; }
    public bool AutoUseEnabled { get; set; }
    public int AutoHpThresholdPercent { get; set; }
}
