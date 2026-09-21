using Game.Shared.Enums;

namespace Game.Server.Configuration;

public sealed class DungeonEncounterOptions
{
    public const string SectionName = "DungeonEncounters";
    public Dictionary<string, List<DungeonWaveOptions>> Dungeons { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DungeonWaveOptions
{
    public List<EncounterMonsterOptions> Monsters { get; set; } = [];
}

public sealed class EncounterMonsterOptions
{
    public string Name { get; set; } = string.Empty;
    public ElementType Element { get; set; } = ElementType.Wind;
    public int MaxHp { get; set; }
    public int Attack { get; set; }
    public int Defense { get; set; }
    public string CombatProfileCode { get; set; } = string.Empty;
}
