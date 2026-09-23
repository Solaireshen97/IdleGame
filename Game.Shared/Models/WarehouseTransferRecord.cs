namespace Game.Shared.Models;

public sealed class WarehouseTransferRecord
{
    public int UserId { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public int CharacterId { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
