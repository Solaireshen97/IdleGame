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
    public string WeaponCode { get; set; } = string.Empty;
}
