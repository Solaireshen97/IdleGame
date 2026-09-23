using Game.Shared.Enums;

namespace Game.Shared.Dtos.Shop;

public sealed class ShopResponse
{
    public int CharacterId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public int Gold { get; set; }
    public int CharacterCount { get; set; }
    public int CharacterSlotLimit { get; set; }
    public int MaximumCharacterSlots { get; set; }
    public int? NextCharacterSlotCost { get; set; }
    public List<ShopItemResponse> Items { get; set; } = [];
    public List<ShopMaterialResponse> Materials { get; set; } = [];
    public List<DungeonExchangeOfferResponse> DungeonExchangeOffers { get; set; } = [];
}

public sealed class ShopItemResponse
{
    public string Code { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Price { get; set; }
    public int OwnedQuantity { get; set; }
    public int? HealAmount { get; set; }
    public int? AttackPercent { get; set; }
    public int? CooldownRounds { get; set; }
    public ElementType? Element { get; set; }
    public int? Attack { get; set; }
    public int? MaxHp { get; set; }
    public List<ShopWeaponSkillResponse> WeaponSkills { get; set; } = [];
}

public sealed class ShopWeaponSkillResponse
{
    public string Name { get; set; } = string.Empty;
    public int Level { get; set; }
    public decimal Percent { get; set; }
    public string Description { get; set; } = string.Empty;
}

public sealed class ShopMaterialResponse
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

public sealed class DungeonExchangeOfferResponse
{
    public string Code { get; set; } = string.Empty;
    public string DungeonCode { get; set; } = string.Empty;
    public string DungeonName { get; set; } = string.Empty;
    public string CurrencyCode { get; set; } = string.Empty;
    public string CurrencyName { get; set; } = string.Empty;
    public int Cost { get; set; }
    public string WeaponCode { get; set; } = string.Empty;
    public string WeaponName { get; set; } = string.Empty;
    public ElementType Element { get; set; }
    public int Attack { get; set; }
    public int MaxHp { get; set; }
    public int OwnedQuantity { get; set; }
    public List<ShopWeaponSkillResponse> WeaponSkills { get; set; } = [];
}

public sealed class DungeonExchangeResultResponse
{
    public ShopResponse Shop { get; set; } = new();
    public string WeaponDisplayName { get; set; } = string.Empty;
}
