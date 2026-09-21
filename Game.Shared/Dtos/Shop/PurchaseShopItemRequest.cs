namespace Game.Shared.Dtos.Shop;

public sealed class PurchaseShopItemRequest
{
    public int CharacterId { get; set; }
    public string Code { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
}
