namespace Game.Shared.Dtos.Gathering;

public sealed class GatheringOverviewResponse
{
    public int CharacterId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public int GatheringLevel { get; set; }
    public DateTime ServerTimeUtc { get; set; }
    public List<GatheringPointResponse> Points { get; set; } = [];
    public GatheringTaskResponse? ActiveTask { get; set; }
    public List<GatheringTaskResponse> RecentTasks { get; set; } = [];
}

public sealed class GatheringPointResponse
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string RegionName { get; set; } = string.Empty;
    public string MaterialCode { get; set; } = string.Empty;
    public bool IsRare { get; set; }
    public int AvailableOpportunities { get; set; }
    public string MaterialName { get; set; } = string.Empty;
    public int CharacterQuantity { get; set; }
    public int OutputQuantity { get; set; }
    public int CycleSeconds { get; set; }
    public int MinimumCharacterLevel { get; set; }
    public int MinimumGatheringLevel { get; set; }
    public string UnlockDescription { get; set; } = string.Empty;
    public int UnlockProgress { get; set; }
    public int UnlockRequired { get; set; }
    public bool IsUnlocked { get; set; }
}

public sealed class GatheringTaskResponse
{
    public int Id { get; set; }
    public string PointCode { get; set; } = string.Empty;
    public bool IsRare { get; set; }
    public string PointName { get; set; } = string.Empty;
    public string MaterialName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int CycleSeconds { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public DateTime NextCycleAtUtc { get; set; }
    public DateTime? StoppedAtUtc { get; set; }
    public int CompletedCycles { get; set; }
    public int TotalQuantity { get; set; }
    public int ExtraYieldQuantity { get; set; }
    public string? BonusMaterialName { get; set; }
    public int BonusQuantity { get; set; }
}
