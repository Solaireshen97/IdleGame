namespace Game.Shared.Dtos.Characters;

public sealed class SetConsumableAutoRequest
{
    public bool AutoUseEnabled { get; set; }
    public string? AutoConditionOverride { get; set; }
    public int AutoHpThresholdPercent { get; set; } = ConsumableRules.DefaultAutoHpThresholdPercent;
}
