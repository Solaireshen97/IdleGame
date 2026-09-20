namespace Game.Shared.Models;

public class BattleSkillCooldown
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public int CharacterId { get; set; }
    public string SkillCode { get; set; } = string.Empty;
    public int ReadyAtRound { get; set; }
}
