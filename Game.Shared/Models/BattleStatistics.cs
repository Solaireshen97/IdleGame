using Game.Shared.Dtos;
using Game.Shared.Enums;

namespace Game.Shared.Models;

public sealed class BattleRunStatistics
{
    public int RoomId { get; set; }
    public int RunSequence { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public int DungeonId { get; set; }
    public int DepthLevel { get; set; }
    public string RulesRevision { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public string Outcome { get; set; } = "InProgress";
    public int CoverageStartRound { get; set; } = 1;
    public long RecordedRounds { get; set; }
    public int LastAggregatedRound { get; set; }
    public int LastSettlementVersion { get; set; }
}

public sealed class BattleEncounterStatistics
{
    public int RoomId { get; set; }
    public int RunSequence { get; set; }
    public int MonsterId { get; set; }
    public string Name { get; set; } = "";
    public int WaveNumber { get; set; }
    public int Position { get; set; }
    public bool IsBoss { get; set; }
    public long RecordedRounds { get; set; }
    public int FirstRound { get; set; }
    public int LastRound { get; set; }
    public long UnattributedDamage { get; set; }
    public long UnattributedHealing { get; set; }
}

public sealed class BattleActorStatistics
{
    public int RoomId { get; set; }
    public int RunSequence { get; set; }
    public int MonsterId { get; set; }
    public int CharacterId { get; set; }
    public int? UserId { get; set; }
    public string Name { get; set; } = "";
    public string ProfessionCode { get; set; } = "";
    public ElementType? Element { get; set; }
    public int? SlotIndex { get; set; }
    public string ConfigurationsJson { get; set; } = "[]";
    public int LastObservedRound { get; set; }
    public bool WasAlive { get; set; }
    public long PresentRounds { get; set; }
    public long AliveRounds { get; set; }
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
}

public sealed class BattleAbilityStatistics
{
    public int RoomId { get; set; }
    public int RunSequence { get; set; }
    public int MonsterId { get; set; }
    public int CharacterId { get; set; }
    public BattleActionKind ActionKind { get; set; }
    public string SourceCode { get; set; } = "";
    public string Label { get; set; } = "";
    public long DamageDealt { get; set; }
    public long HealingDone { get; set; }
    public long SelfHealing { get; set; }
    public long PotionHealing { get; set; }
}
