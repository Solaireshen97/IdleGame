namespace Game.Shared.Models;

public sealed class UserWarehouseStack
{
    public int UserId { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public int Version { get; set; }
}
