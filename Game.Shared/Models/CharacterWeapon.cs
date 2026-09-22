using Game.Shared.Enums;

namespace Game.Shared.Models;

public sealed class CharacterWeapon
{
    public int Id { get; set; }
    public int CharacterId { get; set; }
    public string WeaponCode { get; set; } = string.Empty;
    public int TemplateRevision { get; set; }
    public WeaponOrigin Origin { get; set; } = WeaponOrigin.Legacy;
    public string Name { get; set; } = string.Empty;
    public ElementType Element { get; set; }
    public int Attack { get; set; }
    public int MaxHp { get; set; }
    public int ItemLevel { get; set; } = 1;
    public int SellGold { get; set; } = 1;
    public int DismantleFragments { get; set; } = 1;
    public bool IsLocked { get; set; }
    public int? EquippedSlotIndex { get; set; }
    public List<CharacterWeaponSkill> Skills { get; set; } = [];
    public int Version { get; set; }
}
