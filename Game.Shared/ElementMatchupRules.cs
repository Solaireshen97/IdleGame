using Game.Shared.Enums;

namespace Game.Shared;

public static class ElementMatchupRules
{
    public static int PlayerAttackPercent(ElementType? player, ElementType monster) =>
        player is null ? 0 : IsLightDarkPair(player.Value, monster) ? 25 : DirectionalPercent(player.Value, monster);

    public static int MonsterAttackPercent(ElementType monster, ElementType? player) =>
        player is null ? 0 : IsLightDarkPair(player.Value, monster) ? -25 : DirectionalPercent(monster, player.Value);

    private static bool IsLightDarkPair(ElementType a, ElementType b) =>
        a == ElementType.Light && b == ElementType.Dark || a == ElementType.Dark && b == ElementType.Light;

    private static int DirectionalPercent(ElementType attacker, ElementType defender) => (attacker, defender) switch
    {
        (ElementType.Fire, ElementType.Wind) or (ElementType.Wind, ElementType.Earth) or
        (ElementType.Earth, ElementType.Water) or (ElementType.Water, ElementType.Fire) => 25,
        (ElementType.Wind, ElementType.Fire) or (ElementType.Earth, ElementType.Wind) or
        (ElementType.Water, ElementType.Earth) or (ElementType.Fire, ElementType.Water) => -25,
        _ => 0
    };
}
