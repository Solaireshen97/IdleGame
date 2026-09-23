namespace Game.Shared.Models;

public sealed class CharacterWeaponSkill
{
    public int Id { get; set; }
    public int WeaponId { get; set; }
    public int SlotIndex { get; set; }
    public string SkillCode { get; set; } = string.Empty;
    public int Level { get; set; }
    public int BaseLevel { get; set; } = 1;
    // Legacy database column. New weapon quality is stored on CharacterWeapon.
    public int QualityBonusLevel { get; set; }
    public int EnhancementLevel { get; set; }
    public int? SpentFragments { get; set; }
}
