namespace Game.Server.Configuration;

public class StoryOptions
{
    public const string SectionName = "Story";
    public List<StoryChapterDefinition> Chapters { get; set; } = [];
    public List<StoryNpcDefinition> Npcs { get; set; } = [];
    public List<StoryQuestDefinition> Quests { get; set; } = [];
    public List<StoryMapNodeDefinition> MapNodes { get; set; } = [];
}

public class StoryChapterDefinition
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FirstQuestCode { get; set; } = string.Empty;
    public string LastQuestCode { get; set; } = string.Empty;
}

public class StoryNpcDefinition
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public class StoryQuestDefinition
{
    public string Code { get; set; } = string.Empty;
    public int Revision { get; set; } = 1;
    public string ChapterCode { get; set; } = string.Empty;
    public int Order { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string StartNpcCode { get; set; } = string.Empty;
    public string TurnInNpcCode { get; set; } = string.Empty;
    public string StartDialogue { get; set; } = string.Empty;
    public string TurnInDialogue { get; set; } = string.Empty;
    public string ActorPolicy { get; set; } = "AnyOwnedCharacter";
    public string ObjectiveType { get; set; } = string.Empty;
    public string TargetCode { get; set; } = string.Empty;
    public int RequiredCount { get; set; } = 1;
    public int RewardGold { get; set; }
    public List<StoryItemReward> Rewards { get; set; } = [];
    public List<string> UnlockMapNodeCodes { get; set; } = [];
    public string? NextQuestCode { get; set; }
    public string Destination { get; set; } = string.Empty;
}

public class StoryItemReward
{
    public string Code { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

public class StoryMapNodeDefinition
{
    public string Code { get; set; } = string.Empty;
    public string DungeonCode { get; set; } = string.Empty;
    public string RegionCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string RequiredQuestCode { get; set; } = string.Empty;
    public bool IsPreview { get; set; }
}
