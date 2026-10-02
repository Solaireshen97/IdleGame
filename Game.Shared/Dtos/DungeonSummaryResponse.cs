using Game.Shared.Enums;

namespace Game.Shared.Dtos;

public class DungeonSummaryResponse
{
    public int DungeonId { get; set; }
    public bool SupportsDepths { get; set; }
    public int Stage { get; set; }
    public int DepthLevel { get; set; } = 1;
    public int MaximumDepth { get; set; } = 1;
    public int UnlockedDepth { get; set; } = 1;
    public int CharacterHighestDepth { get; set; }
    public int MasteryLevel { get; set; }
    public decimal GoldBonusPercent { get; set; }
    public decimal KillExtraRollChancePercent { get; set; }
    public decimal ClearExtraRollChancePercent { get; set; }
    public bool UsesPlaceholderBalance { get; set; }
    public List<DungeonDepthPreviewResponse> Depths { get; set; } = [];
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string RegionName { get; set; } = string.Empty;
    public string RegionCode { get; set; } = string.Empty;
    public string DungeonKind { get; set; } = string.Empty;
    public string PartyScalingProfileCode { get; set; } = "fixed";
    public List<int> PartyHpPercentages { get; set; } = [];
    public string Description { get; set; } = string.Empty;
    public int MinimumLevel { get; set; }
    public int RecommendedLevel { get; set; }
    public int CurrentCharacterLevel { get; set; }
    public bool CanEnter { get; set; }
    public string? LockReason { get; set; }
    public string MonsterName { get; set; } = string.Empty;
    public ElementType MonsterElement { get; set; }
    public int MonsterMaxHp { get; set; }
    public int MonsterAttack { get; set; }
    public int MonsterDefense { get; set; }
    public int SlotCount { get; set; }
    public int WaveCount { get; set; }
    public int MonsterCount { get; set; }
    public bool IsClearedByCurrentUser { get; set; }
    public bool AutoUnlocked { get; set; }
    public List<MonsterPreviewResponse> Monsters { get; set; } = [];
    public List<DungeonRewardPreviewResponse> RewardPreview { get; set; } = [];
}

public sealed class DungeonDepthPreviewResponse
{
    public int DepthLevel { get; set; }
    public bool IsUnlocked { get; set; }
    public bool IsChallenge { get; set; }
    public decimal StatMultiplier { get; set; }
    public bool UsesExplicitStats { get; set; }
    public List<string> AddedMechanics { get; set; } = [];
}

public sealed class DungeonRewardPreviewResponse
{
    public string Source { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal ChancePercent { get; set; }
    public WeaponDropPreviewResponse? Weapon { get; set; }
}
