namespace Game.Shared.Dtos.Shop;

public sealed class ExchangeDungeonWeaponRequest
{
    public int CharacterId { get; set; }
    public string OfferCode { get; set; } = string.Empty;
    public string? RequestId { get; set; }
}
