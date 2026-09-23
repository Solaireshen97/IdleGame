namespace Game.Shared.Dtos.Warehouse;

public sealed class WarehouseResponse
{
    public int CharacterId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public List<WarehouseItemResponse> Items { get; set; } = [];
}

public sealed class WarehouseItemResponse
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public bool CanTransfer { get; set; }
    public int CharacterQuantity { get; set; }
    public int WarehouseQuantity { get; set; }
}
