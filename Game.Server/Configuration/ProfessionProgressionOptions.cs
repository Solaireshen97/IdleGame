namespace Game.Server.Configuration;

public sealed class ProfessionProgressionOptions
{
    public const string SectionName = "Professions";
    public int MaximumLevel { get; set; } = 10;
    public List<int> ExperienceToNextLevel { get; set; } = [];
    public int GatheringExperiencePerCycle { get; set; } = 1;
    public int RareGatheringExperiencePerCycle { get; set; } = 10;
    public int AlchemyExperiencePerCycle { get; set; } = 2;
    public List<ProfessionTalentNodeOptions> TalentNodes { get; set; } = [];
}

public sealed class ProfessionTalentNodeOptions
{
    public string Code { get; set; } = string.Empty;
    public string ProfessionCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string EffectType { get; set; } = string.Empty;
    public int ValuePerRank { get; set; }
    public int MaxRank { get; set; } = 1;
    public int MinimumLevel { get; set; } = 2;
    public int Tier { get; set; } = 1;
    public string? PrerequisiteCode { get; set; }
    public int PrerequisiteRank { get; set; } = 1;
}
