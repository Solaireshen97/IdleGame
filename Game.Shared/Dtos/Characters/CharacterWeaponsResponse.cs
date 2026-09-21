using Game.Shared.Enums;

namespace Game.Shared.Dtos.Characters;

public sealed class CharacterWeaponsResponse
{
    public int CharacterId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string ProfessionName { get; set; } = string.Empty;
    public int Hp { get; set; }
    public int TotalAttack { get; set; }
    public int TotalMaxHp { get; set; }
    public int EffectiveAttack { get; set; }
    public int EffectiveMaxHp { get; set; }
    public int Defense { get; set; }
    public ElementType? MainElement { get; set; }
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
    public int? EquippedSlotIndex { get; set; }
}
