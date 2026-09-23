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
    public int FinalDamagePercent { get; set; }
    public int DamageTakenPercent { get; set; }
    public int NormalAttackDamagePercent { get; set; }
    public int AreaDamageReductionPercent { get; set; }
    public string? WeaponSkillCode { get; set; }
    public int WeaponSkillLevel { get; set; }
    public int DurationRounds { get; set; }
    public int Tier { get; set; } = 1;
    public int CooldownRounds { get; set; }
    public string CooldownGroup { get; set; } = string.Empty;
}
