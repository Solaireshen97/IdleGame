using Game.Shared.Enums;
using Game.Shared.Models;

namespace Game.Server.Configuration;

public sealed class WorldOptions
{
    public const string SectionName = "World";
    public List<RegionOptions> Regions { get; set; } = [];
    public List<Dungeon> Dungeons { get; set; } = [];
}

public sealed class RegionOptions
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int MinimumLevel { get; set; } = 1;
    public int MaximumLevel { get; set; } = 10;
    public ElementType FeaturedElement { get; set; }
    public string FeaturedDungeonCode { get; set; } = string.Empty;
    public string FeaturedWeaponCode { get; set; } = string.Empty;
}
