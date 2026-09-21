namespace Game.Shared.Models;

public sealed class MonsterIntent
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public int RunSequence { get; set; }
    public int RoundNumber { get; set; }
    public int MonsterId { get; set; }
    public string ActionType { get; set; } = "BasicAttack";
    public string? SkillCode { get; set; }
    public string TargetType { get; set; } = "Front";
    public int? TargetCharacterId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
