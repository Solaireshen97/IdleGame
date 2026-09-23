namespace Game.Server.Configuration;

public sealed class ConsumableOptions
{
    public const string SectionName = "Consumables";

    public List<ConsumableItemOptions> Items { get; set; } = [];
}

public sealed class ConsumableItemOptions
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "Healing";
    public int HealAmount { get; set; }
    public decimal HealMaxHpPercent { get; set; }
    public int AttackPercent { get; set; }
    public int CooldownRounds { get; set; }
    public string CooldownGroup { get; set; } = string.Empty;
}
