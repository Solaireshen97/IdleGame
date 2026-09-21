using Game.Shared.Enums;

namespace Game.Shared.Dtos.Shop;

public sealed class ShopResponse
{
    public int CharacterId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public int Gold { get; set; }
    public List<ShopItemResponse> Items { get; set; } = [];
}

public sealed class ShopItemResponse
{
    public string Code { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Price { get; set; }
    public int OwnedQuantity { get; set; }
    public int? HealAmount { get; set; }
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
}
