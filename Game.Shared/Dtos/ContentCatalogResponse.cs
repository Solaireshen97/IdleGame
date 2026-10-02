using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;

namespace Game.Shared.Dtos;

public sealed class ContentCatalogResponse
{
    public int SchemaVersion { get; set; } = 1;
    public string Version { get; set; } = "";
    public List<ProfessionResponse> Professions { get; set; } = [];
    public List<RegionSummaryResponse> Regions { get; set; } = [];
    public List<DungeonDefinitionResponse> Dungeons { get; set; } = [];
}

// This contract contains definitions only. Admission, clears and mastery belong
// to DungeonProgressResponse and must never enter a public content cache.
public sealed class DungeonDefinitionResponse
{
    public int DungeonId { get; set; }
    public bool SupportsDepths { get; set; }
    public int Stage { get; set; }
    public int MaximumDepth { get; set; } = 1;
    public bool UsesPlaceholderBalance { get; set; }
    public List<DungeonDepthDefinitionResponse> Depths { get; set; } = [];
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string RegionName { get; set; } = "";
    public string RegionCode { get; set; } = "";
    public string DungeonKind { get; set; } = "";
    public string PartyScalingProfileCode { get; set; } = "fixed";
    public List<int> PartyHpPercentages { get; set; } = [];
    public string Description { get; set; } = "";
    public int MinimumLevel { get; set; }
    public int RecommendedLevel { get; set; }
    public string MonsterName { get; set; } = "";
    public ElementType MonsterElement { get; set; }
    public int MonsterMaxHp { get; set; }
    public int MonsterAttack { get; set; }
    public int MonsterDefense { get; set; }
    public int SlotCount { get; set; }
    public int WaveCount { get; set; }
    public int MonsterCount { get; set; }
    public List<MonsterPreviewResponse> Monsters { get; set; } = [];
    public List<DungeonRewardPreviewResponse> RewardPreview { get; set; } = [];

    public static DungeonDefinitionResponse FromSummary(DungeonSummaryResponse source) => new()
    {
        DungeonId = source.DungeonId, SupportsDepths = source.SupportsDepths, Stage = source.Stage,
        MaximumDepth = source.MaximumDepth, UsesPlaceholderBalance = source.UsesPlaceholderBalance,
        Depths = source.Depths.Select(depth => new DungeonDepthDefinitionResponse
        {
            DepthLevel = depth.DepthLevel, IsChallenge = depth.IsChallenge,
            StatMultiplier = depth.StatMultiplier, UsesExplicitStats = depth.UsesExplicitStats, AddedMechanics = depth.AddedMechanics.ToList()
        }).ToList(),
        Code = source.Code, Name = source.Name, RegionName = source.RegionName, RegionCode = source.RegionCode,
        DungeonKind = source.DungeonKind, PartyScalingProfileCode = source.PartyScalingProfileCode,
        PartyHpPercentages = source.PartyHpPercentages.ToList(), Description = source.Description,
        MinimumLevel = source.MinimumLevel, RecommendedLevel = source.RecommendedLevel,
        MonsterName = source.MonsterName, MonsterElement = source.MonsterElement,
        MonsterMaxHp = source.MonsterMaxHp, MonsterAttack = source.MonsterAttack,
        MonsterDefense = source.MonsterDefense, SlotCount = source.SlotCount,
        WaveCount = source.WaveCount, MonsterCount = source.MonsterCount,
        Monsters = source.Monsters, RewardPreview = source.RewardPreview
    };

    public DungeonSummaryResponse WithProgress(DungeonProgressEntry? progress, int characterLevel) => new()
    {
        DungeonId = DungeonId, SupportsDepths = SupportsDepths, Stage = Stage, MaximumDepth = MaximumDepth,
        UsesPlaceholderBalance = UsesPlaceholderBalance,
        Depths = Depths.Select(depth => new DungeonDepthPreviewResponse
        {
            DepthLevel = depth.DepthLevel, IsChallenge = depth.IsChallenge, StatMultiplier = depth.StatMultiplier,
            UsesExplicitStats = depth.UsesExplicitStats,
            AddedMechanics = depth.AddedMechanics.ToList(), IsUnlocked = depth.DepthLevel <= (progress?.UnlockedDepth ?? 0)
        }).ToList(),
        Code = Code, Name = Name, RegionName = RegionName, RegionCode = RegionCode, DungeonKind = DungeonKind,
        PartyScalingProfileCode = PartyScalingProfileCode, PartyHpPercentages = PartyHpPercentages.ToList(),
        Description = Description, MinimumLevel = MinimumLevel, RecommendedLevel = RecommendedLevel,
        MonsterName = MonsterName, MonsterElement = MonsterElement, MonsterMaxHp = MonsterMaxHp,
        MonsterAttack = MonsterAttack, MonsterDefense = MonsterDefense, SlotCount = SlotCount,
        WaveCount = WaveCount, MonsterCount = MonsterCount, Monsters = Monsters, RewardPreview = RewardPreview,
        CurrentCharacterLevel = characterLevel, UnlockedDepth = progress?.UnlockedDepth ?? 0,
        CharacterHighestDepth = progress?.CharacterHighestDepth ?? 0, MasteryLevel = progress?.MasteryLevel ?? 0,
        GoldBonusPercent = progress?.GoldBonusPercent ?? 0,
        KillExtraRollChancePercent = progress?.KillExtraRollChancePercent ?? 0,
        ClearExtraRollChancePercent = progress?.ClearExtraRollChancePercent ?? 0,
        CanEnter = progress?.CanEnter ?? false, LockReason = progress is null ? "请先选择角色" : progress.LockReason,
        IsClearedByCurrentUser = progress?.IsClearedByCurrentUser ?? false, AutoUnlocked = progress?.AutoUnlocked ?? false
    };
}

public sealed class DungeonDepthDefinitionResponse
{
    public int DepthLevel { get; set; }
    public bool IsChallenge { get; set; }
    public decimal StatMultiplier { get; set; }
    public bool UsesExplicitStats { get; set; }
    public List<string> AddedMechanics { get; set; } = [];
}

public sealed class DungeonProgressResponse
{
    public int UserId { get; set; }
    public int? CharacterId { get; set; }
    public int CurrentCharacterLevel { get; set; }
    public List<DungeonProgressEntry> Dungeons { get; set; } = [];
}

public sealed class DungeonProgressEntry
{
    public int DungeonId { get; set; }
    public int UnlockedDepth { get; set; }
    public int CharacterHighestDepth { get; set; }
    public int MasteryLevel { get; set; }
    public decimal GoldBonusPercent { get; set; }
    public decimal KillExtraRollChancePercent { get; set; }
    public decimal ClearExtraRollChancePercent { get; set; }
    public bool CanEnter { get; set; }
    public string? LockReason { get; set; }
    public bool IsClearedByCurrentUser { get; set; }
    public bool AutoUnlocked { get; set; }
}
