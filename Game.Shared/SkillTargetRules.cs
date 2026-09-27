namespace Game.Shared;

public static class SkillTargetRules
{
    public static bool UsesChosenTarget(string effectType, string effectTarget) =>
        (effectType, effectTarget) is ("Heal", "LowestHpAlly") or ("Guard", "FrontAlly") or
            ("Cleanse", "FirstDebuffedAlly") or ("ApplyStatus", "FrontAlly");

    public static bool CanChooseAllyTarget(IEnumerable<(string Type, string Target)> effects)
    {
        var items = effects.ToArray();
        // A group skill keeps its whole-party scope, including its accompanying cleanse.
        return !items.Any(effect => effect.Target is "AllAlive" or "AllAllies") &&
            items.Any(effect => UsesChosenTarget(effect.Type, effect.Target));
    }
}
