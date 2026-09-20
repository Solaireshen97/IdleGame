namespace Game.Shared.Models;

public class BattleConsumableCooldown
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public int CharacterId { get; set; }
    public string CooldownGroup { get; set; } = string.Empty;
    public int ReadyAtRound { get; set; }
}
