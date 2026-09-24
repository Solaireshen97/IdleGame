namespace Game.Server.Configuration;

public sealed class DungeonExchangeOptions
{
    public const string SectionName = "DungeonExchange";
    public List<DungeonExchangeOfferOptions> Offers { get; set; } = [];
}

public sealed class DungeonExchangeOfferOptions
{
    public string Code { get; set; } = string.Empty;
    public string DungeonCode { get; set; } = string.Empty;
    public string DungeonName { get; set; } = string.Empty;
    public string CurrencyCode { get; set; } = string.Empty;
    public int Cost { get; set; }
    public string RewardKind { get; set; } = "Weapon";
    public string RewardCode { get; set; } = string.Empty;
    public int RewardQuantity { get; set; } = 1;

    // Kept for existing configuration. New non-weapon rewards use RewardCode.
    public string WeaponCode { get; set; } = string.Empty;

    public string EffectiveRewardCode =>
        string.Equals(RewardKind, "Weapon", StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrWhiteSpace(RewardCode)
            ? WeaponCode
            : RewardCode;
}
