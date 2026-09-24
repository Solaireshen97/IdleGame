using Game.Shared.Enums;

namespace Game.Server.Configuration;

public sealed class SoulImprintOptions
{
    public const string SectionName = "SoulImprints";
    public List<SoulImprintDefinitionOptions> Items { get; set; } = [];
}

public sealed class SoulImprintDefinitionOptions
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string DungeonCode { get; set; } = string.Empty;
    public int Tier { get; set; } = 1;
    public ElementType Element { get; set; }
    public SoulImprintEffectType EffectType { get; set; }
    public int PowerPercent { get; set; }
    public int SecondaryPowerPercent { get; set; }
    public int DurationRounds { get; set; }
    public int InitialCooldownRounds { get; set; }
    public int CooldownRounds { get; set; }
    public int DismantleFragments { get; set; }
}
