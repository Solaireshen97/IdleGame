namespace Game.Shared.Dtos;

public sealed class CoopDropBonusPreviewResponse
{
    public int TeamPlayerCount { get; set; }
    public decimal BonusPercent { get; set; }
    public decimal PerAdditionalPlayerPercent { get; set; }
    public decimal MaximumPercent { get; set; }
}
