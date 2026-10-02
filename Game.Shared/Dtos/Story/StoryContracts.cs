namespace Game.Shared.Dtos.Story;

public class StoryOverviewResponse
{
    public int UserId { get; set; }
    public int Version { get; set; }
    public int? TutorialCharacterId { get; set; }
    public string? TutorialCharacterName { get; set; }
    public bool NeedsTutorialCharacter { get; set; }
    public bool IsLegacy { get; set; }
    public string ChapterCode { get; set; } = string.Empty;
    public string ChapterName { get; set; } = string.Empty;
    public bool ChapterCompleted { get; set; }
    public StoryQuestResponse? CurrentQuest { get; set; }
    public List<StoryQuestResponse> Quests { get; set; } = [];
    public List<StoryMapNodeResponse> MapNodes { get; set; } = [];
}

public class StoryQuestResponse
{
    public string Code { get; set; } = string.Empty;
    public string ChapterCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Status { get; set; } = "Locked";
    public int Version { get; set; }
    public int Progress { get; set; }
    public int RequiredCount { get; set; }
    public int? ActorCharacterId { get; set; }
    public string NpcName { get; set; } = string.Empty;
    public string StartDialogue { get; set; } = string.Empty;
    public string TurnInDialogue { get; set; } = string.Empty;
    public int RewardGold { get; set; }
    public List<StoryItemRewardResponse> Rewards { get; set; } = [];
    public string Destination { get; set; } = string.Empty;
}

public class StoryItemRewardResponse
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

public class StoryMapNodeResponse
{
    public string Code { get; set; } = string.Empty;
    public string DungeonCode { get; set; } = string.Empty;
    public int? DungeonId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RegionCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool CanEnter { get; set; }
    public bool IsUnlocked { get; set; }
    public bool IsCleared { get; set; }
    public string? LockReason { get; set; }
    public string? RequiredQuestCode { get; set; }
    public int RecommendedLevel { get; set; }
}

public class StoryTurnInRequest
{
    public string RequestId { get; set; } = string.Empty;
    public int ExpectedVersion { get; set; }
}

public class StoryTutorialCharacterRequest
{
    public int CharacterId { get; set; }
    public int ExpectedVersion { get; set; }
    public string RequestId { get; set; } = string.Empty;
}
