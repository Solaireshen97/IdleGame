namespace Game.Server.Configuration;

public sealed class ShopOptions
{
    public const string SectionName = "Shop";
    public List<ShopItemOptions> Items { get; set; } = [];
}

public sealed class ShopItemOptions
{
    public string Code { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int Price { get; set; }
    public string? UnlockKind { get; set; }
    public string UnlockTargetCode { get; set; } = string.Empty;
    public List<string> AlternativeUnlockTargetCodes { get; set; } = [];
    public int RequiredCount { get; set; } = 1;
    public int MinimumCharacterLevel { get; set; } = 1;
}
