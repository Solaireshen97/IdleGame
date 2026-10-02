using Game.Shared.Enums;

namespace Game.Shared.Dtos.Characters;

public sealed class CharacterSoulImprintsResponse
{
    public int CharacterId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public List<CharacterSoulImprintResponse> SoulImprints { get; set; } = [];
    public List<WeaponFragmentResponse> Fragments { get; set; } = [];
    public Game.Shared.Dtos.Inventory.InventoryOperationResult? OperationResult { get; set; }
}

public sealed class CharacterSoulImprintResponse
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Tier { get; set; }
    public ElementType Element { get; set; }
    public SoulImprintEffectType EffectType { get; set; }
    public int PowerPercent { get; set; }
    public int SecondaryPowerPercent { get; set; }
    public int DurationRounds { get; set; }
    public int InitialCooldownRounds { get; set; }
    public int CooldownRounds { get; set; }
    public int DismantleFragments { get; set; }
    public bool IsEquipped { get; set; }
    public bool AutoUseEnabled { get; set; }
    public string AutoCondition { get; set; } = "Always";
    public string DefaultAutoCondition { get; set; } = "Always";
    public string? AutoConditionOverride { get; set; }
    public int AutoHpThresholdPercent { get; set; } = SkillRules.DefaultAutoHpThresholdPercent;
    public bool IsLocked { get; set; }
}

public sealed class SetSoulImprintRequest
{
    public int? SoulImprintId { get; set; }
}

public sealed class SetSoulImprintLockRequest
{
    public bool IsLocked { get; set; }
}

public sealed class SetSoulImprintAutoRequest
{
    public bool AutoUseEnabled { get; set; }
    public string? AutoConditionOverride { get; set; }
    // Omitted by older clients when toggling Auto; preserve their existing condition and threshold.
    public int? AutoHpThresholdPercent { get; set; }
}

public sealed class SoulImprintBatchRequest
{
    public List<int> SoulImprintIds { get; set; } = [];
    public string? RequestId { get; set; }
    public List<Game.Shared.Dtos.Inventory.InventoryInstanceVersion> ExpectedVersions { get; set; } = [];
    public string? OutcomeFingerprint { get; set; }
}
