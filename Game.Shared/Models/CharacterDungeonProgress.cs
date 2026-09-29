namespace Game.Shared.Models;

public sealed class CharacterDungeonProgress
{
    public int CharacterId { get; set; }
    public int DungeonId { get; set; }
    public int HighestDepth { get; set; }
    public int Version { get; set; }
}

public sealed class CharacterFirstHuntWeaponClaim
{
    public int CharacterId { get; set; }
    public int DungeonId { get; set; }
}

// The snapshot belongs to a character and attempt, not a mutable formation slot.
public sealed class DungeonRunParticipant
{
    public int RoomId { get; set; }
    public int RunSequence { get; set; }
    public int CharacterId { get; set; }
    public int MasteryLevel { get; set; }
}
