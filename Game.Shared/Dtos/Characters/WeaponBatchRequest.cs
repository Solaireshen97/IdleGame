namespace Game.Shared.Dtos.Characters;

public sealed class WeaponBatchRequest
{
    public List<int> WeaponIds { get; set; } = [];
    public string? RequestId { get; set; }
    public List<Game.Shared.Dtos.Inventory.InventoryInstanceVersion> ExpectedVersions { get; set; } = [];
    public string? OutcomeFingerprint { get; set; }
}
