namespace Game.Shared;

public static class ConsumableRules
{
    public const int SlotCount = 2;
    public const int HealingPotionSlotIndex = 1;
    public const int BuffPotionSlotIndex = 2;
    public const int OperationPotionSlotIndex = 3;
    public const int TotalSlotCount = 3;
    public const int HealingPotionUsesPerRun = 2;
    public const int DefaultAutoHpThresholdPercent = 50;

    public static int SlotMask(int slotIndex) => slotIndex is >= 1 and <= SlotCount
        ? 1 << (slotIndex - 1) : 0;

    public static bool CanEquip(int slotIndex, string? kind) => (slotIndex, kind) is
        (HealingPotionSlotIndex, "Healing") or (BuffPotionSlotIndex, "CombatBuff") or
        (OperationPotionSlotIndex, "OperationPotion");

    public static string SlotName(int slotIndex) => slotIndex switch
    {
        HealingPotionSlotIndex => "治疗药水",
        BuffPotionSlotIndex => "强化药水",
        OperationPotionSlotIndex => "合剂",
        _ => "补给"
    };

    public static int TierForLevel(int level) => level > 0
        ? (level - 1) / 10 + 1
        : throw new ArgumentOutOfRangeException(nameof(level));

    public static int EffectScalePercent(int itemTier, int characterLevel)
    {
        if (itemTier < 1) throw new ArgumentOutOfRangeException(nameof(itemTier));
        return (TierForLevel(characterLevel) - itemTier) switch
        {
            <= 0 => 100,
            1 => 75,
            2 => 25,
            _ => 0
        };
    }

    public static int ScaledPercent(int value, int itemTier, int characterLevel) =>
        (int)decimal.Floor(value * EffectScalePercent(itemTier, characterLevel) / 100m);

    public static int ScaledSkillLevel(int level, int itemTier, int characterLevel) =>
        (int)decimal.Round(level * EffectScalePercent(itemTier, characterLevel) / 100m,
            MidpointRounding.AwayFromZero);
}
