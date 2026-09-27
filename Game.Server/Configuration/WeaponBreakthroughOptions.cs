namespace Game.Server.Configuration;

public sealed class WeaponBreakthroughOptions
{
    public const string SectionName = "WeaponBreakthrough";
    public List<WeaponBreakthroughRecipeOptions> Recipes { get; set; } = [];
}

public sealed class WeaponBreakthroughRecipeOptions
{
    public int Tier { get; set; }
    public string FragmentCode { get; set; } = string.Empty;
    public string StoneCode { get; set; } = string.Empty;
    public int FragmentsPerStone { get; set; }
    public int MinimumWeaponItemLevel { get; set; }
    public int MaximumWeaponItemLevel { get; set; }
}
