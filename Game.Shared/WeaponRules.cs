using Game.Shared.Enums;

namespace Game.Shared;

public static class WeaponRules
{
    public const int SlotCount = 10;
    public const int MainSlotIndex = 1;
    public const int MaxSkillsPerWeapon = 3;
    public const int MaxSkillLevel = 20;
    public const int MaxQualityBonusLevels = 3;
    public const int MaxEnhancementPerSkill = 3;
    public const int MaxEnhancementWithQuality = MaxEnhancementPerSkill + MaxQualityBonusLevels;

    public static int EnhancementLimit(int qualityRank, int baseLevel) =>
        Math.Min(MaxEnhancementPerSkill + Math.Clamp(qualityRank, 0, MaxQualityBonusLevels),
            MaxSkillLevel - baseLevel);

    public static int FragmentTier(int itemLevel)
    {
        if (itemLevel < 1) throw new ArgumentOutOfRangeException(nameof(itemLevel));
        return (itemLevel - 1) / 10 + 1;
    }

    public static string FragmentCode(int tier) => tier > 0
        ? $"weapon-fragment-t{tier}"
        : throw new ArgumentOutOfRangeException(nameof(tier));

    public static string FragmentName(int tier) => $"T{tier} 武器碎片";

    public static string QualityName(int bonusLevels) => bonusLevels switch
    {
        <= 0 => "普通",
        1 => "精良",
        2 => "稀有",
        _ => "史诗"
    };

    public static string QualityCode(int bonusLevels) => bonusLevels switch
    {
        <= 0 => "common",
        1 => "uncommon",
        2 => "rare",
        _ => "epic"
    };

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
