using Game.Shared.Enums;

namespace Game.Server.Configuration;

public sealed class MonsterCombatOptions
{
    public const string SectionName = "MonsterCombat";
    public List<BattleStatusOptions> StatusEffects { get; set; } = [];
    public List<MonsterSkillOptions> Skills { get; set; } = [];
    public Dictionary<string, MonsterCombatProfileOptions> Profiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<MonsterDepthStageOptions>> DepthProgressions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class BattleStatusOptions
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string EffectType { get; set; } = string.Empty;
    public decimal ValuePerStack { get; set; }
    public int MaxStacks { get; set; } = 1;
    public string Stacking { get; set; } = "RefreshDuration";
    public bool IsPositive { get; set; }
    public bool IsDispellable { get; set; } = true;
    public bool IsHidden { get; set; }
    // Unspecified scope preserves existing general vulnerability, including frozen rooms.
    public BattleDamageScope DamageScope { get; set; } = BattleDamageScope.All;
    public BattleStatusLifetime Lifetime { get; set; }
    public BattleStatusCounterKind CounterKind { get; set; }
    public BattleStatusMechanic Mechanic { get; set; }
    public BattleStatusSnapshotRefresh SnapshotRefresh { get; set; }
    public string? FamilyCode { get; set; }
    public BattleStatusFamilyRefresh FamilyRefresh { get; set; }
    public int InitialStacks { get; set; } = 1;
    public int MechanicLevel { get; set; }
    public decimal MechanicPower { get; set; }
}

public sealed class MonsterSkillOptions
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TargetType { get; set; } = "Front";
    public List<CombatSkillEffectOptions>? Effects { get; set; }
    public int InitialCooldownRounds { get; set; }
    public int DamagePowerPercent { get; set; }
    public int CooldownRounds { get; set; }
    public int? SelfHpBelowPercent { get; set; }
    public int? RoomRoundAtLeast { get; set; }
    public int ForcedPriority { get; set; }
    public bool IsInterruptible { get; set; } = true;
    public string DangerLevel { get; set; } = "Normal";
    public List<MonsterStatusApplicationOptions> Statuses { get; set; } = [];
}

public sealed class MonsterStatusApplicationOptions
{
    public string StatusCode { get; set; } = string.Empty;
    public int DurationRounds { get; set; }
}

public sealed class MonsterCombatProfileOptions
{
    public int SkillUseChancePercent { get; set; }
    // Opt in so existing profiles and frozen room snapshots keep their original clock.
    public bool UseEncounterLocalSkillClock { get; set; }
    public List<MonsterProfileSkillOptions> Skills { get; set; } = [];
    public string? DepthProgressionCode { get; set; }
    public FireCoreOptions? FireCore { get; set; }
    public DeepColdOptions? DeepCold { get; set; }
    public EarthArmorOptions? EarthArmor { get; set; }
    public StaticFieldOptions? StaticField { get; set; }
    public ReflectionMirrorOptions? ReflectionMirror { get; set; }
    public PlaguePoisonOptions? PlaguePoison { get; set; }

    internal IMonsterPhaseSchedule? Phase => (IMonsterPhaseSchedule?)FireCore ?? DeepCold ??
        (IMonsterPhaseSchedule?)EarthArmor ?? StaticField ?? (IMonsterPhaseSchedule?)ReflectionMirror ?? PlaguePoison;
}

public sealed class PlaguePoisonOptions : IMonsterPhaseSchedule
{
    // Frozen LV2/LV3 declarations keep their single HP trigger; LV4 uses a local schedule.
    public int? TriggerHpPercent { get; set; }
    public int FirstActivationRound { get; set; }
    public int CycleRounds { get; set; }
    public int WindowRounds { get; set; }
    public decimal BreakLightDamagePercent { get; set; }
    public decimal AttackPercentPerStack { get; set; }
    public string PoisonStatusCode { get; set; } = string.Empty;
    public string TargetStatusCode { get; set; } = string.Empty;
    public string TickUsedStatusCode { get; set; } = string.Empty;
    public string RewardStatusCode { get; set; } = string.Empty;
    public int RewardRounds { get; set; }
    public List<string> BasicPoisonStatusCodes { get; set; } = [];
    // Missing linkage keeps frozen LV2 rooms at ordinary four-round poison.
    public int ErosionStartStacks { get; set; }
    public decimal ErosionPercentPerStack { get; set; }
    public string ErosionStatusCode { get; set; } = string.Empty;
    public int LethalStacks { get; set; }
}

public sealed class ReflectionMirrorOptions : IMonsterPhaseSchedule
{
    // LV2/LV3 keep one HP trigger; LV4 follows the persistent encounter-local schedule.
    public int? TriggerHpPercent { get; set; }
    public int FirstActivationRound { get; set; }
    public int CycleRounds { get; set; }
    public int WindowRounds { get; set; }
    public int InitialStacks { get; set; }
    public ElementType RemovalElement { get; set; } = ElementType.Dark;
    public decimal ReflectPercentPerStack { get; set; }
    public decimal MaxHpCapPercentPerStack { get; set; }
    public string MirrorStatusCode { get; set; } = string.Empty;
    public string HitUsedStatusCode { get; set; } = string.Empty;
    public string BudgetStatusCode { get; set; } = string.Empty;
    public string RewardStatusCode { get; set; } = string.Empty;
    public int RewardRounds { get; set; }
    // Optional LV3 linkage. Missing fields keep frozen LV2 rooms at fixed reflection strength.
    public int GrowthRounds { get; set; }
    public decimal ReflectGrowthPercentPerStack { get; set; }
    public decimal MaxHpCapGrowthPercentPerStack { get; set; }
    public string AmplificationStatusCode { get; set; } = string.Empty;
}

public sealed class StaticFieldOptions : IMonsterPhaseSchedule
{
    // LV2/LV3 retain a one-shot HP trigger; LV4 uses a fixed encounter-local schedule.
    public int? TriggerHpPercent { get; set; }
    public int FirstActivationRound { get; set; }
    public int CycleRounds { get; set; }
    public ElementType RemovalElement { get; set; } = ElementType.Fire;
    public int WindowRounds { get; set; }
    public int InitialStacks { get; set; }
    public int GrowthRounds { get; set; }
    public int GrowthStacksPerRound { get; set; }
    public string StaticStatusCode { get; set; } = string.Empty;
    public string HitUsedStatusCode { get; set; } = string.Empty;
    public string GrowthUsedStatusCode { get; set; } = string.Empty;
    public string AmplifiedSkillCode { get; set; } = string.Empty;
    public decimal SkillDamagePercentPerStack { get; set; }
    public string RewardStatusCode { get; set; } = string.Empty;
    public int RewardRounds { get; set; }
    // LV3 optional linkage. Missing fields preserve existing LV2 frozen rooms.
    public int ThunderAtStacks { get; set; }
    public string ThunderSkillCode { get; set; } = string.Empty;
    public string ThunderPendingStatusCode { get; set; } = string.Empty;
}

public sealed class EarthArmorOptions : IMonsterPhaseSchedule
{
    // LV2/LV3: one HP trigger. LV4: a fixed encounter-local schedule instead.
    public int? TriggerHpPercent { get; set; }
    public int FirstActivationRound { get; set; }
    public int CycleRounds { get; set; }
    public int WindowRounds { get; set; }
    public decimal BreakWindDamagePercent { get; set; }
    public string ArmorStatusCode { get; set; } = string.Empty;
    public string RewardStatusCode { get; set; } = string.Empty;
    public int RewardRounds { get; set; }
    // LV3 optional linkage: armor-round ends build persistent attack stacks; a break clears them.
    public string ResonanceStatusCode { get; set; } = string.Empty;
}

public sealed class DeepColdOptions : IMonsterPhaseSchedule
{
    public int? TriggerHpPercent { get; set; }
    public int FirstActivationRound { get; set; }
    public int CycleRounds { get; set; }
    // Frozen water rooms authored before attribute alignment retain Fire when the field is absent.
    public ElementType RemovalElement { get; set; } = ElementType.Fire;
    public int WindowRounds { get; set; }
    public int InitialStacks { get; set; }
    public int RewardRounds { get; set; }
    public string ColdStatusCode { get; set; } = string.Empty;
    public string FieldStatusCode { get; set; } = string.Empty;
    public string WarmStatusCode { get; set; } = string.Empty;
    public string PendingStatusCode { get; set; } = string.Empty;
    public string ClearedStatusCode { get; set; } = string.Empty;
    public string MeltUsedStatusCode { get; set; } = string.Empty;
    public List<string> BasicColdStatusCodes { get; set; } = [];
    public int GrowthStacksPerRound { get; set; }
    public int FreezeAtStacks { get; set; }
    public string FreezeStatusCode { get; set; } = string.Empty;
    public string FreezePendingStatusCode { get; set; } = string.Empty;
    public string FreezeUsedStatusCode { get; set; } = string.Empty;
    public string GrowthUsedStatusCode { get; set; } = string.Empty;
}

public sealed class FireCoreOptions : IMonsterPhaseSchedule
{
    // When set, activate once at the next player-round start at or below this HP percentage.
    public int? TriggerHpPercent { get; set; }
    public int FirstActivationRound { get; set; }
    public int CycleRounds { get; set; }
    public int WindowRounds { get; set; }
    public decimal BreakWaterDamagePercent { get; set; }
    public string HeatingStatusCode { get; set; } = string.Empty;
    public string RewardStatusCode { get; set; } = string.Empty;
    public int RewardRounds { get; set; }
    public string LinkedSkillCode { get; set; } = string.Empty;
    public int ExtraTargetCount { get; set; }
    public int ExtraAttackPowerPercent { get; set; }
}

public sealed class MonsterDepthStageOptions
{
    public int Depth { get; set; }
    public string? ReplacementProfileCode { get; set; }
    public List<MonsterProfileSkillOptions> AddedSkills { get; set; } = [];
}

public sealed class MonsterProfileSkillOptions
{
    public string Code { get; set; } = string.Empty;
    public int Weight { get; set; } = 1;
}
