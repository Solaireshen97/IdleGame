using Game.Shared.Enums;

namespace Game.Shared.Dtos;

public enum BattleEventKind { Damage, Heal, Status, Cleanse, Dispel, Interrupt, Cooldown, Defeat }
public enum BattleActionKind { Skill, NormalAttack, SoulImprint, Counter, FollowUp, Periodic, Mechanic, Consumable }
public enum BattleStatusChange { Added, Refreshed, Consumed, Removed, Expired, Retained }

public sealed record BattleEventActor(string ActorType, int ActorId, int? SlotIndex = null,
    string? Name = null, string? ProfessionCode = null);

/// <summary>A frozen fact from a successful settlement. Names are presentation only; identities are authoritative.</summary>
public sealed record BattleEventResponse
{
    public long Id { get; init; }
    public int Sequence { get; init; }
    public int RoomId { get; init; }
    public int RunSequence { get; init; }
    public int RoundNumber { get; init; }
    public int MonsterId { get; init; }
    public int SettlementVersion { get; init; }
    public BattleEventKind Kind { get; init; }
    public BattleActionKind ActionKind { get; init; }
    public BattleEventActor? Source { get; init; }
    public required BattleEventActor Target { get; init; }
    public string? SkillCode { get; init; }
    public string Label { get; init; } = string.Empty;
    public int CalculatedAmount { get; init; }
    public int ActualAmount { get; init; }
    public int? HpBefore { get; init; }
    public int? HpAfter { get; init; }
    public int? TargetMaxHp { get; init; }
    public bool IsCritical { get; init; }
    public ElementType? Element { get; init; }
    public decimal ElementModifier { get; init; }
    public BattleStatusChange? StatusChange { get; init; }
    public int? CountBefore { get; init; }
    public int? CountAfter { get; init; }
    public BattleStatusSnapshot? Status { get; init; }
}

public sealed record BattleStatusSnapshot
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string EffectType { get; init; } = string.Empty;
    public bool IsPositive { get; init; }
    public bool CanDispel { get; init; }
    public int Stacks { get; init; }
    public int RemainingRounds { get; init; }
    public BattleStatusLifetime Lifetime { get; init; }
    public BattleStatusCounterKind CounterKind { get; init; }
    public BattleStatusMechanic Mechanic { get; init; }
    public int AppliedRound { get; init; }
    public int ExpiresAfterRound { get; init; }
    public int? PerTickValue { get; init; }
    public decimal? MagnitudeSnapshot { get; init; }
    public string DurationText { get; init; } = string.Empty;
    public string CounterText { get; init; } = string.Empty;
    public string? SourceActorType { get; init; }
    public int? SourceActorId { get; init; }
    public string? SourceSkillCode { get; init; }
    public string? BoundTargetType { get; init; }
    public int? BoundTargetId { get; init; }
    public string? BoundTargetName { get; init; }

    public BattleStatusEffectResponse ToResponse() => new()
    {
        Code = Code, Name = Name, Description = Description, EffectType = EffectType, IsPositive = IsPositive, CanDispel = CanDispel,
        Stacks = Stacks, RemainingRounds = RemainingRounds, DurationText = DurationText, CounterText = CounterText,
        Lifetime = Lifetime, CounterKind = CounterKind, Mechanic = Mechanic,
        ExpiresWithRun = Lifetime is BattleStatusLifetime.UntilConsumed or BattleStatusLifetime.Encounter or BattleStatusLifetime.Run,
        PerTickValue = PerTickValue, MagnitudeSnapshot = MagnitudeSnapshot, SourceActorType = SourceActorType,
        SourceActorId = SourceActorId, SourceSkillCode = SourceSkillCode, BoundTargetType = BoundTargetType,
        BoundTargetId = BoundTargetId, BoundTargetName = BoundTargetName
    };
}
