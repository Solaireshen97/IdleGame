using Game.Shared.Enums;

namespace Game.Server.Configuration;

public sealed class WeaponOptions
{
    public const string SectionName = "Weapons";
    public List<WeaponTemplateOptions> Items { get; set; } = [];
    public List<WeaponSkillDefinitionOptions> Skills { get; set; } = [];
    public Dictionary<string, List<string>> StarterPacks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WeaponTemplateOptions
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ElementType Element { get; set; }
    public int Attack { get; set; }
    public int MaxHp { get; set; }
    public List<WeaponSkillGrantOptions> Skills { get; set; } = [];
}

public sealed class WeaponSkillDefinitionOptions
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public WeaponSkillEffectType EffectType { get; set; }
    public decimal PercentPerLevel { get; set; }
}

public sealed class WeaponSkillGrantOptions
{
    public string Code { get; set; } = string.Empty;
    public int Level { get; set; }
}
