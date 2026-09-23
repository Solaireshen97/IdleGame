namespace Game.Shared.Models;

public sealed class CharacterGatheringOpportunity
{
    public int CharacterId { get; set; }
    public string PointCode { get; set; } = string.Empty;
    public int AvailableCount { get; set; }
    public int EarnedCount { get; set; }
    public int SpentCount { get; set; }
    public int Version { get; set; }
}
