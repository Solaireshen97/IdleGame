namespace Game.Shared;

public static class SkillRules
{
    public const int SlotCount = 5;
    public const int DefaultAutoHpThresholdPercent = 70;
    public const string DefaultProfessionCode = "knight";

    public static int SlotMask(int slotIndex) => 1 << (slotIndex - 1);
}
