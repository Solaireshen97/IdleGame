using Game.Shared.Enums;

namespace Game.Shared.Dtos.Characters;

public sealed class CharacterWeaponsResponse
{
    public int CharacterId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string ProfessionName { get; set; } = string.Empty;
    public int Gold { get; set; }
    public int Hp { get; set; }
    public int TotalAttack { get; set; }
    public int TotalMaxHp { get; set; }
    public int EffectiveAttack { get; set; }
    public int EffectiveMaxHp { get; set; }
    public int Defense { get; set; }
    public ElementType? MainElement { get; set; }
    public decimal AttackBonusPercent { get; set; }
    public decimal HealthBonusPercent { get; set; }
    public decimal CriticalChancePercent { get; set; }
    public List<ActiveWeaponSkillResponse> ActiveSkills { get; set; } = [];
    public List<WeaponEffectResponse> ActiveEffects { get; set; } = [];
    public List<WeaponFragmentResponse> Fragments { get; set; } = [];
    public List<CharacterWeaponResponse> Weapons { get; set; } = [];
}

public sealed class CharacterWeaponResponse
{
    public int Id { get; set; }
    public string WeaponCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ElementType Element { get; set; }
    public int Attack { get; set; }
    public int MaxHp { get; set; }
    public int ItemLevel { get; set; }
    public int FragmentTier { get; set; }
    public int SellGold { get; set; }
    public bool CanSell { get; set; } = true;
    public WeaponOrigin Origin { get; set; }
    public int DismantleFragments { get; set; }
    public int DismantleReturnQuantity { get; set; }
    public bool CanDismantle { get; set; }
    public int QualityBonusLevel { get; set; }
    public string QualityName { get; set; } = string.Empty;
    public string QualityCode { get; set; } = string.Empty;
    public bool IsLocked { get; set; }
    public int? EquippedSlotIndex { get; set; }
    public List<WeaponSkillResponse> Skills { get; set; } = [];
}

public sealed class WeaponSkillResponse
{
    public int SlotIndex { get; set; }
    public string SkillCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Level { get; set; }
    public int BaseLevel { get; set; }
    public int QualityBonusLevel { get; set; }
    public int EnhancementLevel { get; set; }
    public int MaximumEnhancementLevel { get; set; }
    public int? NextEnhancementCost { get; set; }
    public decimal TotalPercent { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

public sealed class WeaponFragmentResponse
{
    public int Tier { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

public sealed class ActiveWeaponSkillResponse
{
    public string SkillCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Level { get; set; }
    public decimal TotalPercent { get; set; }
    public string Description { get; set; } = string.Empty;
}

public sealed class WeaponEffectResponse
{
    public WeaponSkillEffectType EffectType { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal EffectiveLevel { get; set; }
    public decimal TotalPercent { get; set; }
}
