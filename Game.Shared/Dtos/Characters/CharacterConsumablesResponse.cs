namespace Game.Shared.Dtos.Characters;

public class CharacterConsumablesResponse
{
    public int CharacterId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public List<ConsumableItemResponse> Items { get; set; } = [];
    public List<ConsumableSlotResponse> Slots { get; set; } = [];
}

public class ConsumableItemResponse
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int HealAmount { get; set; }
    public int CooldownRounds { get; set; }
    public int Quantity { get; set; }
}

public class ConsumableSlotResponse
{
    public int SlotIndex { get; set; }
    public string? ItemCode { get; set; }
    public bool AutoUseEnabled { get; set; }
    public int AutoHpThresholdPercent { get; set; }
}
