using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;

namespace Game.Shared.Dtos.Inventory;

public static class InventoryKinds
{
    public const string Weapon = "weapon";
    public const string SoulImprint = "soul";
    public const string Stack = "stack";
}

public static class InventoryCategories
{
    public const string All = "all";
    public const string Weapons = "weapons";
    public const string SoulImprints = "souls";
    public const string Supplies = "supplies";
    public const string Planting = "planting";
    public const string Upgrade = "upgrade";
    public const string Exchange = "exchange";
    public const string Other = "other";
    public static readonly string[] Values = [Weapons, SoulImprints, Supplies, Planting, Upgrade, Exchange, Other];
    public static string Name(string category) => category switch
    {
        All => "全部", Weapons => "武器", SoulImprints => "魂印", Supplies => "补给",
        Planting => "种植材料", Upgrade => "养成材料", Exchange => "兑换材料", _ => "其他"
    };
}

public sealed class InventoryQueryRequest
{
    public string Category { get; set; } = InventoryCategories.All;
    public string? Search { get; set; }
    public ElementType? Element { get; set; }
    public int? Tier { get; set; }
    public int? QualityRank { get; set; }
    public string State { get; set; } = "all";
    public string Sort { get; set; } = "name";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 40;
}

public sealed class InventoryOverviewResponse
{
    public int CharacterId { get; set; }
    public string CharacterName { get; set; } = "";
    public DateTime GeneratedAtUtc { get; set; }
    public int Gold { get; set; }
    public long WeaponCount { get; set; }
    public long SoulImprintCount { get; set; }
    public long OwnedStackKinds { get; set; }
    public long EquippedCount { get; set; }
    public long ProtectedCount { get; set; }
    public bool IsLoadoutLocked { get; set; }
    public List<InventoryCategorySummary> Categories { get; set; } = [];
    public int FilteredRowCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<InventoryEntryDto> Items { get; set; } = [];
    public InventoryPendingSummary Pending { get; set; } = new();
}

public sealed class InventoryCategorySummary
{
    public string Category { get; set; } = "";
    public long RowCount { get; set; }
    public long Quantity { get; set; }
    public string Unit { get; set; } = "种";
}

public sealed class InventoryPendingSummary
{
    public int MaturePlotCount { get; set; }
    public int PendingBattleCount { get; set; }
    public long PendingGold { get; set; }
    public List<InventoryRewardDto> BattleRewards { get; set; } = [];
}

public sealed class InventoryEntryDto
{
    public string Key { get; set; } = "";
    public string AssetKind { get; set; } = "";
    public string Code { get; set; } = "";
    public int? InstanceId { get; set; }
    public int Version { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = InventoryCategories.Other;
    public List<string> Tags { get; set; } = [];
    public int Quantity { get; set; }
    public bool IsDefinitionKnown { get; set; }
    public bool HasCodeConflict { get; set; }
    public int? Tier { get; set; }
    public int? ItemLevel { get; set; }
    public int? Attack { get; set; }
    public int? MaxHp { get; set; }
    public int? QualityRank { get; set; }
    public ElementType? Element { get; set; }
    public int? EquippedSlotIndex { get; set; }
    public bool IsEquipped { get; set; }
    public bool IsLocked { get; set; }
    public List<InventoryFormationReferenceDto> FormationReferences { get; set; } = [];
    public List<InventoryActionAvailabilityDto> Actions { get; set; } = [];
    public List<InventoryUsageDto> Usages { get; set; } = [];
}

public sealed class InventoryFormationReferenceDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class InventoryUsageDto
{
    public string Label { get; set; } = "";
    public string Route { get; set; } = "";
}

public sealed class InventoryActionAvailabilityDto
{
    public string Action { get; set; } = "";
    public bool Allowed { get; set; }
    public List<string> ReasonCodes { get; set; } = [];
}

public sealed class InventoryItemDetailDto
{
    public int CharacterId { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
    public InventoryEntryDto Entry { get; set; } = new();
    public CharacterWeaponResponse? Weapon { get; set; }
    public CharacterSoulImprintResponse? SoulImprint { get; set; }
    public List<CharacterWeaponResponse> QualityMaterials { get; set; } = [];
    public List<WeaponFragmentResponse> Fragments { get; set; } = [];
    public List<WeaponBreakthroughMaterialResponse> BreakthroughMaterials { get; set; } = [];
}

public sealed class InventoryActionPreviewRequest
{
    public string AssetKind { get; set; } = "";
    public string Action { get; set; } = "";
    public List<int> InstanceIds { get; set; } = [];
}

public sealed class InventoryInstanceVersion
{
    public int Id { get; set; }
    public int Version { get; set; }
}

public sealed class InventoryRewardDto
{
    public string Kind { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public long Quantity { get; set; }
}

public sealed class InventoryActionPreviewResponse
{
    public int CharacterId { get; set; }
    public string AssetKind { get; set; } = "";
    public string Action { get; set; } = "";
    public bool Allowed { get; set; }
    public List<InventoryEntryDto> Items { get; set; } = [];
    public List<InventoryInstanceVersion> ExpectedVersions { get; set; } = [];
    public List<InventoryRewardDto> Rewards { get; set; } = [];
    public string OutcomeFingerprint { get; set; } = "";
}

public sealed class InventoryOperationResult
{
    public string RequestId { get; set; } = "";
    public string AssetKind { get; set; } = "";
    public string Action { get; set; } = "";
    public List<int> InstanceIds { get; set; } = [];
    public List<InventoryRewardDto> Rewards { get; set; } = [];
}
