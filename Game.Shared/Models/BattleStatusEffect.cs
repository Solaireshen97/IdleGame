namespace Game.Shared.Models;

public sealed class BattleStatusEffect
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public int RunSequence { get; set; }
    public string TargetType { get; set; } = string.Empty;
    public int TargetId { get; set; }
    public string EffectCode { get; set; } = string.Empty;
    public int Stacks { get; set; } = 1;
    public int AppliedRound { get; set; }
    public int ExpiresAfterRound { get; set; }
}
