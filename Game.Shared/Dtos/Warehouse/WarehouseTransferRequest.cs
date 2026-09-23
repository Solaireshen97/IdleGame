namespace Game.Shared.Dtos.Warehouse;

public sealed class WarehouseTransferRequest
{
    public int CharacterId { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public Guid RequestId { get; set; }
}
