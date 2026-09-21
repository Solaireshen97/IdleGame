namespace Game.Shared.Models;

public sealed class CharacterWeaponSkill
{
    public int Id { get; set; }
    public int WeaponId { get; set; }
    public int SlotIndex { get; set; }
    public string SkillCode { get; set; } = string.Empty;
    public int Level { get; set; }
}
