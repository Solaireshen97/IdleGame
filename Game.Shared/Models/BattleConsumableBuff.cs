namespace Game.Shared.Models;

public sealed class BattleConsumableBuff
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public int RunSequence { get; set; }
    public int CharacterId { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string WeaponSkillCode { get; set; } = string.Empty;
    public int SkillLevel { get; set; }
    public int AppliedRound { get; set; }
    public int ExpiresAfterRound { get; set; }
}
