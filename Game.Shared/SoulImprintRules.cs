namespace Game.Shared;

public static class SoulImprintRules
{
    public const int SlotIndex = 1;
    public const string CooldownPrefix = "soul-imprint:";

    public static string CooldownCode(string soulImprintCode) => $"{CooldownPrefix}{soulImprintCode}";
}
