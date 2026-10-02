using Game.Shared.Enums;

namespace Game.Shared.Dtos;

public sealed class BattleStatisticsResponse
{
    public int RoomId { get; set; }
    public int RoomVersion { get; set; }
    public int CurrentRunSequence { get; set; }
    public string Scope { get; set; } = "current";
    public int? RunSequence { get; set; }
    public int? MonsterId { get; set; }
    public int? CharacterId { get; set; }
    public string StatisticsRevision { get; set; } = "";
    public int SchemaVersion { get; set; } = 1;
    // Complete, Partial, Unrecorded, or NotStarted; an empty response is not zero contribution.
    public string Coverage { get; set; } = "NotStarted";
    public int? CoverageStartRound { get; set; }
    public long RecordedRounds { get; set; }
    public int LastRoundNumber { get; set; }
    public int RecordedRuns { get; set; }
    public int CompletedRuns { get; set; }
    public int MissingRuns { get; set; }
    public int PartialRuns { get; set; }
    public string Outcome { get; set; } = "InProgress";
    public bool IsClosed { get; set; }
    public long UnattributedDamage { get; set; }
    public long UnattributedHealing { get; set; }
    public BattleStatisticsMetrics Totals { get; set; } = new();
    public List<BattleStatisticsEncounterResponse> Encounters { get; set; } = [];
    public List<BattleStatisticsActorResponse> Actors { get; set; } = [];
}

public sealed class BattleStatisticsMetrics
{
    public long DamageDealt { get; set; }
    public long DamageTaken { get; set; }
    public long HealingDone { get; set; }
    public long HealingReceived { get; set; }
    public long SelfHealing { get; set; }
    public long PotionHealing { get; set; }
    public long Deaths { get; set; }
    public long Cleanses { get; set; }
    public long Dispels { get; set; }
    public long Interrupts { get; set; }
    public long PresentRounds { get; set; }
    public long AliveRounds { get; set; }
}

public sealed record BattleStatisticsConfiguration
{
    public string ProfessionCode { get; init; } = "";
    public ElementType? Element { get; init; }
    public string? FormationName { get; init; }
    public int? FormationVersion { get; init; }
    public string LoadoutHash { get; init; } = "";
}

public sealed class BattleStatisticsActorResponse
{
    public int CharacterId { get; set; }
    public string Name { get; set; } = "";
    public string ProfessionCode { get; set; } = "";
    public ElementType? Element { get; set; }
    public int? SlotIndex { get; set; }
    public bool IsOwn { get; set; }
    public bool IsPresent { get; set; }
    public bool WasAlive { get; set; }
    public bool HasUnknownIdentity { get; set; }
    public bool HasMixedConfigurations { get; set; }
    public List<BattleStatisticsConfiguration> Configurations { get; set; } = [];
    public BattleStatisticsMetrics Metrics { get; set; } = new();
    public decimal? DamagePerRound { get; set; }
    public decimal? DamageSharePercent { get; set; }
    public List<BattleStatisticsAbilityResponse> Abilities { get; set; } = [];
}

public sealed class BattleStatisticsAbilityResponse
{
    public BattleActionKind ActionKind { get; set; }
    public string SourceCode { get; set; } = "";
    public string Label { get; set; } = "";
    public long DamageDealt { get; set; }
    public long HealingDone { get; set; }
    public long SelfHealing { get; set; }
    public long PotionHealing { get; set; }
}

public sealed class BattleStatisticsEncounterResponse
{
    public int MonsterId { get; set; }
    public string Name { get; set; } = "";
    public int WaveNumber { get; set; }
    public int Position { get; set; }
    public bool IsBoss { get; set; }
    public long RecordedRounds { get; set; }
}
