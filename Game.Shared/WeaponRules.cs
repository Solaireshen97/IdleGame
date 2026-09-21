using Game.Shared.Enums;

namespace Game.Shared;

public static class WeaponRules
{
    public const int SlotCount = 10;
    public const int MainSlotIndex = 1;
    public const int MaxSkillsPerWeapon = 3;
    public const int MaxSkillLevel = 20;

    public static string ElementName(ElementType element) => element switch
    {
        ElementType.Fire => "火",
        ElementType.Water => "水",
        ElementType.Earth => "土",
        ElementType.Wind => "风",
        ElementType.Light => "光",
        ElementType.Dark => "暗",
        _ => "未知"
    };
}
