using Game.Shared.Enums;

namespace Game.Shared.Dtos;

public sealed class MonsterPreviewResponse
{
    public string Name { get; set; } = string.Empty;
    public ElementType Element { get; set; }
    public int MaxHp { get; set; }
    public int Attack { get; set; }
    public int Defense { get; set; }
    public int WaveNumber { get; set; }
    public int Position { get; set; }
    public bool IsBoss { get; set; }
    public List<DungeonRewardPreviewResponse> Drops { get; set; } = [];
}

public sealed class WeaponDropPreviewResponse
{
    public ElementType Element { get; set; }
    public int ItemLevel { get; set; }
    public int Attack { get; set; }
    public int MaxHp { get; set; }
    public List<WeaponDropSkillPreviewResponse> Skills { get; set; } = [];
}

public sealed class WeaponDropSkillPreviewResponse
{
    public string Name { get; set; } = string.Empty;
    public int Level { get; set; }
    public string Description { get; set; } = string.Empty;
}
