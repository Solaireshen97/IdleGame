using Game.Shared.Enums;

namespace Game.Shared.Dtos;

public sealed class RegionSummaryResponse
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int MinimumLevel { get; set; }
    public int MaximumLevel { get; set; }
    public ElementType FeaturedElement { get; set; }
    public string FeaturedDungeonCode { get; set; } = string.Empty;
    public string FeaturedDungeonName { get; set; } = string.Empty;
    public string FeaturedWeaponName { get; set; } = string.Empty;
}
