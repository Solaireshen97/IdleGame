namespace Game.Shared;

public static class SkillRules
{
    public const int SlotCount = 5;
    public const int DefaultAutoHpThresholdPercent = 70;
    public const string DefaultProfessionCode = "swordsman";
    public const int PromotionLevel = 10;
    public const int SharedSkillEquipLevel = 10;
    public const int SharedSkillUnlockLevel = 30;

    public static int SlotMask(int slotIndex) => 1 << (slotIndex - 1);

    // Non-damage interrupt skills need a live interrupt window, regardless of Auto preferences.
    public static bool RequiresInterruptibleTarget(IEnumerable<string> effectTypes)
    {
        var hasInterrupt = false;
        foreach (var type in effectTypes)
        {
            if (type == "Damage") return false;
            if (type == "Interrupt") hasInterrupt = true;
        }
        return hasInterrupt;
    }
}
