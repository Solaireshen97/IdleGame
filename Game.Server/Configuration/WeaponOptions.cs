using Game.Shared.Enums;

namespace Game.Server.Configuration;

public sealed class WeaponOptions
{
    public const string SectionName = "Weapons";
    public int StartingAccountGold { get; set; }
    public List<WeaponTemplateOptions> Items { get; set; } = [];
    public List<WeaponSkillDefinitionOptions> Skills { get; set; } = [];
    public List<WeaponEffectRuleOptions> EffectRules { get; set; } = [];
    public Dictionary<string, List<string>> StarterPacks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Replacements { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<int> EnhancementFragmentCosts { get; set; } = [];
    public List<WeaponSkillGrowthSegmentOptions> SkillGrowth { get; set; } = [];
    public WeaponDropQualityWeightsOptions DropQualityWeights { get; set; } = new();
}

public sealed class WeaponDropQualityWeightsOptions
{
    public int Common { get; set; } = 60;
    public int Uncommon { get; set; } = 25;
    public int Rare { get; set; } = 12;
    public int Epic { get; set; } = 3;
}

public sealed class WeaponTemplateOptions
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ElementType Element { get; set; }
    public int Attack { get; set; }
    public int MaxHp { get; set; }
    public int ItemLevel { get; set; } = 1;
    public int Revision { get; set; }
    public int SellGold { get; set; } = 1;
    public int DismantleFragments { get; set; } = 1;
    public List<WeaponSkillGrantOptions> Skills { get; set; } = [];
}

public sealed class WeaponSkillGrowthSegmentOptions
{
    public int? MaximumLevel { get; set; }
    public decimal MultiplierPercent { get; set; }
}

public sealed class WeaponSkillDefinitionOptions
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public WeaponSkillEffectType EffectType { get; set; }
    public decimal PercentPerLevel { get; set; }
    public List<WeaponSkillEffectOptions> Effects { get; set; } = [];
}

public sealed class WeaponSkillEffectOptions
{
    public WeaponSkillEffectType EffectType { get; set; }
    public decimal LevelWeight { get; set; } = 1m;
}

public sealed class WeaponEffectRuleOptions
{
    public WeaponSkillEffectType EffectType { get; set; }
    public decimal PercentPerLevel { get; set; }
    public decimal MaximumPercent { get; set; }
}

public sealed class WeaponSkillGrantOptions
{
    public string Code { get; set; } = string.Empty;
    public int Level { get; set; }
}
