namespace Game.Shared.Models;

public sealed class UserStoryState
{
    public int UserId { get; set; }
    public bool IsLegacy { get; set; }
    public int? TutorialCharacterId { get; set; }
    public string? CurrentQuestCode { get; set; }
    public string ChapterCode { get; set; } = "ch01";
    public bool ChapterCompleted { get; set; }
    public string StoryFlagsJson { get; set; } = "[]";
    public int Version { get; set; }
}

public sealed class StoryQuestProgress
{
    public int UserId { get; set; }
    public string QuestCode { get; set; } = string.Empty;
    public string DefinitionJson { get; set; } = string.Empty;
    public int? ActorCharacterId { get; set; }
    public string Status { get; set; } = "Active";
    public int Progress { get; set; }
    public DateTime ActivatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? TurnedInAtUtc { get; set; }
    public int Version { get; set; }
}

public sealed class StoryMapUnlock
{
    public int UserId { get; set; }
    public string MapNodeCode { get; set; } = string.Empty;
    public string SourceQuestCode { get; set; } = string.Empty;
    public DateTime UnlockedAtUtc { get; set; }
}

public sealed class StoryActionReceipt
{
    public int UserId { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string? QuestCode { get; set; }
    public int RecipientCharacterId { get; set; }
    public string RewardsJson { get; set; } = string.Empty;
    public DateTime CompletedAtUtc { get; set; }
}

public sealed class StoryEventReceipt
{
    public int UserId { get; set; }
    public string QuestCode { get; set; } = string.Empty;
    public string SourceEventId { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
}
