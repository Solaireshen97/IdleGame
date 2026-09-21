namespace Game.Server.Configuration;

public sealed class MonsterCombatOptions
{
    public const string SectionName = "MonsterCombat";
    public List<BattleStatusOptions> StatusEffects { get; set; } = [];
    public List<MonsterSkillOptions> Skills { get; set; } = [];
    public Dictionary<string, MonsterCombatProfileOptions> Profiles { get; set; } =
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
}

public sealed class MonsterSkillOptions
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TargetType { get; set; } = "Front";
    public int DamagePowerPercent { get; set; }
    public int CooldownRounds { get; set; }
    public int? SelfHpBelowPercent { get; set; }
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
}

public sealed class MonsterProfileSkillOptions
{
    public string Code { get; set; } = string.Empty;
    public int Weight { get; set; } = 1;
}
