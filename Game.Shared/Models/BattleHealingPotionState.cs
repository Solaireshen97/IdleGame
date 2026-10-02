namespace Game.Shared.Models;

public sealed class BattleHealingPotionState
{
    public int RoomId { get; set; }
    public int RunSequence { get; set; }
    public int CharacterId { get; set; }
    public int UsesUsed { get; set; }
    public int BuffUsesUsed { get; set; }
    public int Version { get; set; }
}
