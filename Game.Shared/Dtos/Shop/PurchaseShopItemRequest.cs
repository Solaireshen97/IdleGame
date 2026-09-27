namespace Game.Shared.Dtos.Shop;

public sealed class PurchaseShopItemRequest
{
    public string RequestId { get; set; } = string.Empty;
    public int CharacterId { get; set; }
    public string Code { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
}
