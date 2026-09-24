namespace Game.Shared.Dtos.Professions;

public sealed class ProfessionProgressResponse
{
    public string ProfessionCode { get; set; } = string.Empty;
    public string ProfessionName { get; set; } = string.Empty;
    public int Level { get; set; }
    public int Experience { get; set; }
    public int? ExperienceToNextLevel { get; set; }
    public int AvailableTalentPoints { get; set; }
    public bool IsTalentLocked { get; set; }
    public List<ProfessionTalentNodeResponse> Nodes { get; set; } = [];
}

public sealed class ProfessionTalentNodeResponse
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Tier { get; set; }
    public int Rank { get; set; }
    public int MaxRank { get; set; }
    public int MinimumLevel { get; set; }
    public string? PrerequisiteCode { get; set; }
    public string? PrerequisiteName { get; set; }
    public int PrerequisiteRank { get; set; }
    public bool CanPurchase { get; set; }
    public string? LockReason { get; set; }
}
