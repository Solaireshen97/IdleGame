namespace Game.Shared.Models;

public class CharacterConsumableSlot
{
    public int Id { get; set; }
    public int CharacterId { get; set; }
    public int SlotIndex { get; set; }
    public string? ItemCode { get; set; }
    public bool AutoUseEnabled { get; set; }
    public int AutoHpThresholdPercent { get; set; } = ConsumableRules.DefaultAutoHpThresholdPercent;
    public int Version { get; set; }
}
