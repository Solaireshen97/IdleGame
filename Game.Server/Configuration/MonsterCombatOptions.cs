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
    public List<MonsterProfileSkillOptions> Skills { get; set; } = [];
    public string? DepthProgressionCode { get; set; }
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
