using Game.Shared.Enums;

namespace Game.Shared.Models;

public class Dungeon
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string RegionName { get; set; } = string.Empty;
    public string RegionCode { get; set; } = string.Empty;
    public string DungeonKind { get; set; } = "Hunt";
    public string PartyScalingProfileCode { get; set; } = "fixed";
    public string Description { get; set; } = string.Empty;
    public int MinimumLevel { get; set; } = 1;
    // Legacy persisted value: experience reference only. Combat admission uses account progress.
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public int ExperienceReferenceLevel => MinimumLevel;
    public int RecommendedLevel { get; set; } = 1;
    public bool IsVisible { get; set; } = true;
    public string MonsterName { get; set; } = string.Empty;
    public ElementType MonsterElement { get; set; } = ElementType.Wind;
    public int MonsterMaxHp { get; set; }
    public int MonsterAttack { get; set; }
    public int MonsterDefense { get; set; }
    public int SlotCount { get; set; } = 5;
    public int SortOrder { get; set; }
}
