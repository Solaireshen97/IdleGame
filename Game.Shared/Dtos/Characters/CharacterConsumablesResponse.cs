namespace Game.Shared.Dtos.Characters;

public class CharacterConsumablesResponse
{
    public int CharacterId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public int HealingPotionUsesLimit { get; set; }
    public int BuffPotionUsesLimit { get; set; }
    public List<ConsumableItemResponse> Items { get; set; } = [];
    public List<ConsumableSlotResponse> Slots { get; set; } = [];
}

public class ConsumableItemResponse
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "Healing";
    public int HealAmount { get; set; }
    public int AttackPercent { get; set; }
    public int CooldownRounds { get; set; }
    public int Tier { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? WeaponSkillCode { get; set; }
    public string DefaultAutoCondition { get; set; } = "Always";
    public int DefaultAutoHpThresholdPercent { get; set; } = ConsumableRules.DefaultAutoHpThresholdPercent;
    public int Quantity { get; set; }
}

public class ConsumableSlotResponse
{
    public int SlotIndex { get; set; }
    public string? ItemCode { get; set; }
    public bool AutoUseEnabled { get; set; }
    public string AutoCondition { get; set; } = "Always";
    public string DefaultAutoCondition { get; set; } = "Always";
    public string? AutoConditionOverride { get; set; }
    public int AutoHpThresholdPercent { get; set; }
}
