namespace Game.Shared.Dtos.Characters;

public sealed class UpgradeWeaponQualityRequest
{
    public string? RequestId { get; set; }
    public int MaterialWeaponId { get; set; }
    public bool UseUniversalStone { get; set; }
}
