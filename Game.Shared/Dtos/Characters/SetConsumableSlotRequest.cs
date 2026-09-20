namespace Game.Shared.Dtos.Characters;

public class SetConsumableSlotRequest
{
    public string? ItemCode { get; set; }
    public bool AutoUseEnabled { get; set; }
    public int AutoHpThresholdPercent { get; set; } = ConsumableRules.DefaultAutoHpThresholdPercent;
}
