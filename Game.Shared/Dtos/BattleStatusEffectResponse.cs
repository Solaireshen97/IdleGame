using Game.Shared.Enums;

namespace Game.Shared.Dtos;

public sealed class BattleStatusEffectResponse
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string EffectType { get; set; } = string.Empty;
    public BattleStatusLifetime Lifetime { get; set; }
    public BattleStatusCounterKind CounterKind { get; set; }
    public BattleStatusMechanic Mechanic { get; set; }
    public bool IsPositive { get; set; }
    public bool CanDispel { get; set; }
    public int Stacks { get; set; }
    public int RemainingRounds { get; set; }
    public bool ExpiresWithRun { get; set; }
    public string DurationText { get; set; } = string.Empty;
    public string CounterText { get; set; } = string.Empty;
    public decimal? MagnitudeSnapshot { get; set; }
    public int? PerTickValue { get; set; }
    public string? SourceActorType { get; set; }
    public int? SourceActorId { get; set; }
    public string? SourceSkillCode { get; set; }
    public string? BoundTargetType { get; set; }
    public int? BoundTargetId { get; set; }
    public string? BoundTargetName { get; set; }
}

public sealed class MonsterIntentResponse
{
    public List<Characters.SkillEffectResponse> Effects { get; set; } = [];
    public string ActionType { get; set; } = string.Empty;
    public string? SkillCode { get; set; }
    public string ActionName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public int? TargetCharacterId { get; set; }
    public string TargetLabel { get; set; } = string.Empty;
    public bool IsInterruptible { get; set; }
    public bool IsInterrupted { get; set; }
    public string DangerLevel { get; set; } = "Normal";
}
