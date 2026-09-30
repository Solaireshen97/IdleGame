using Game.Shared.Enums;

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
    public int? PerTickValue { get; set; }
    public decimal? MagnitudeSnapshot { get; set; }
    public string? SourceActorType { get; set; }
    public int? SourceActorId { get; set; }
    public string? SourceSkillCode { get; set; }
    public string? BoundTargetType { get; set; }
    public int? BoundTargetId { get; set; }
    public BattleStatusLifetime Lifetime { get; set; }
}
