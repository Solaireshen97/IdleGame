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
}

public sealed class CombatSkillOptions
{
    public string Code { get; set; } = string.Empty;
    public string ProfessionCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string EffectType { get; set; } = string.Empty;
    public int Power { get; set; }
    public int CooldownRounds { get; set; }
}

public sealed class SkillTalentNodeOptions
{
    public string Code { get; set; } = string.Empty;
    public string ProfessionCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SkillCode { get; set; } = string.Empty;
    public int Cost { get; set; } = 1;
    public int Tier { get; set; }
    public int Column { get; set; }
    public List<string> Prerequisites { get; set; } = [];
}
