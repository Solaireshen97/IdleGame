namespace Game.Shared.Models;

public sealed class BattleMonsterSkillCooldown
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public int MonsterId { get; set; }
    public string SkillCode { get; set; } = string.Empty;
    public int ReadyAtRound { get; set; }
}
