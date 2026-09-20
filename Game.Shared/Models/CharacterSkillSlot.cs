namespace Game.Shared.Models;

public class CharacterSkillSlot
{
    public int Id { get; set; }
    public int CharacterId { get; set; }
    public int SlotIndex { get; set; }
    public string? SkillCode { get; set; }
    public bool AutoUseEnabled { get; set; }
    public int AutoHpThresholdPercent { get; set; } = 70;
    public int Version { get; set; }
}
