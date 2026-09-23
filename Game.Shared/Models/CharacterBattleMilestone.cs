namespace Game.Shared.Models;

public sealed class CharacterBattleMilestone
{
    public int CharacterId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string TargetCode { get; set; } = string.Empty;
    public int Count { get; set; }
    public DateTime FirstAtUtc { get; set; }
    public DateTime LastAtUtc { get; set; }
}
