namespace Game.Server.Configuration;

public sealed class SkillOptions
{
    public const string SectionName = "Skills";
    public List<ProfessionOptions> Professions { get; set; } = [];
    public List<CombatSkillOptions> Abilities { get; set; } = [];
    public List<SkillTalentNodeOptions> TalentNodes { get; set; } = [];
}

public sealed class ProfessionOptions
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> StartingSkills { get; set; } = [];
    public bool IsPromotion { get; set; }
    public string? BaseProfessionCode { get; set; }
    public int RequiredLevel { get; set; } = 1;
}

public sealed class CombatSkillOptions
{
    public string Code { get; set; } = string.Empty;
    public string ProfessionCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string EffectType { get; set; } = string.Empty;
    public int Power { get; set; }
    public decimal AttackPowerPercent { get; set; } = 100;
    public decimal HealMaxHpPercent { get; set; }
    public int CooldownRounds { get; set; }
    public string AutoCondition { get; set; } = string.Empty;
    public List<CombatSkillEffectOptions> Effects { get; set; } = [];
}

public sealed class CombatSkillEffectOptions
{
    public string Type { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public int Power { get; set; }
    public decimal AttackPowerPercent { get; set; } = 100;
    public decimal HealMaxHpPercent { get; set; }
    public string? StatusCode { get; set; }
    public int DurationRounds { get; set; }
}

public sealed class SkillTalentNodeOptions
{
    public string Code { get; set; } = string.Empty;
    public string ProfessionCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? SkillCode { get; set; }
    public int Cost { get; set; } = 1;
    public int MaxRank { get; set; } = 1;
    public int Tier { get; set; }
    public int Column { get; set; }
    public int RequiredLevel { get; set; } = 1;
    public string BranchCode { get; set; } = "shared";
    public string? ExclusiveGroup { get; set; }
    public string? EffectCode { get; set; }
    public decimal ValuePerRank { get; set; }
    public List<string> Prerequisites { get; set; } = [];
    public Game.Shared.Enums.TalentType RequiredTalentType { get; set; }
    public int RequiredTalentRank { get; set; }
}
