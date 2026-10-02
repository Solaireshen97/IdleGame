namespace Game.Shared.Dtos.Characters;

public sealed class CraftWeaponBreakthroughStoneRequest
{
    public string? RequestId { get; set; }
    public int Tier { get; set; } = 1;
    public int Quantity { get; set; } = 1;
}
