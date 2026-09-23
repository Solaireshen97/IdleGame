namespace Game.Shared.Models;

public sealed class BattleOperationPotionState
{
    public int RoomId { get; set; }
    public int RunSequence { get; set; }
    public int CharacterId { get; set; }
    public string? ItemCode { get; set; }
    public int AttackPercent { get; set; }
}
