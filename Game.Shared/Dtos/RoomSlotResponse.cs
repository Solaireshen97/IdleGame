namespace Game.Shared.Dtos;

public class RoomSlotResponse
{
    public int SlotIndex { get; set; }
    public int? CharacterId { get; set; }
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
