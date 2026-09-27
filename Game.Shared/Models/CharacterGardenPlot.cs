namespace Game.Shared.Models;
public sealed class CharacterGardenPlot
{
    public int Id { get; set; }
    public int CharacterId { get; set; }
    public int PlotIndex { get; set; }
    public int Version { get; set; }
    public string? PlantCode { get; set; }
    public string? PlantName { get; set; }
    public string? SeedCode { get; set; }
    public string? MaterialCode { get; set; }
    public int HarvestQuantity { get; set; }
    public int GrowthSeconds { get; set; }
    public DateTime? PlantedAtUtc { get; set; }
    public DateTime? MaturesAtUtc { get; set; }
}
