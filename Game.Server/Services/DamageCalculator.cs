using Game.Shared.Enums;

namespace Game.Server.Services;

// Each percentage belongs to one independent multiplier zone. Bonuses within a zone
// can be summed by the caller before the zones are multiplied together.
public readonly record struct DamageFactors(
    decimal AttackPercent = 0,
    decimal HealthPercent = 0,
    decimal CriticalPercent = 0,
    decimal ElementPercent = 0,
    decimal ReductionPercent = 0,
    decimal SkillDamagePercent = 0);

public static class DamageCalculator
{
    public static int Calculate(int attack, int defense, int skillPower = 0, DamageFactors factors = default,
        decimal attackPowerPercent = 100)
    {
        var attackMultiplier = Math.Max(0m, 1m + factors.AttackPercent / 100m);
        var healthMultiplier = Math.Max(0m, 1m + factors.HealthPercent / 100m);
        var criticalMultiplier = Math.Max(0m, 1m + factors.CriticalPercent / 100m);
        var elementMultiplier = Math.Max(0m, 1m + factors.ElementPercent / 100m);
        var reductionMultiplier = Math.Max(0m, 1m - factors.ReductionPercent / 100m);
        var skillMultiplier = Math.Max(0m, 1m + factors.SkillDamagePercent / 100m);

        // Skill power is flat attack. Defense is removed before the remaining zones.
        var afterDefense = Math.Max(1m, (attack * Math.Max(0, attackPowerPercent) / 100m + skillPower) * attackMultiplier - defense);
        var final = afterDefense * healthMultiplier * criticalMultiplier * elementMultiplier * reductionMultiplier * skillMultiplier;
        return Math.Max(1, (int)Math.Min(int.MaxValue, decimal.Floor(final)));
    }
}

public static class WeaponCombatRules
{
    public static decimal HealthDamagePercent(int hp, int maxHp, decimal stamina, decimal enmity)
    {
        if (hp <= 0 || maxHp <= 0) return 0;
        var ratio = Math.Clamp(hp / (decimal)maxHp, 0, 1);
        return stamina * Math.Clamp((ratio - .75m) / .25m, 0, 1) +
               enmity * Math.Clamp((.5m - ratio) / .5m, 0, 1);
    }

    public static bool RollPercent(decimal chance, Random? random = null) =>
        chance >= 100 || chance > 0 && (decimal)(random ?? Random.Shared).NextDouble() * 100m < chance;

    // Echo receives an already resolved normal hit: never apply damage zones twice.
    public static int EchoDamage(int normalDamage, decimal echoPercent) =>
        (int)Math.Min(int.MaxValue, decimal.Floor(Math.Max(0, normalDamage) * Math.Max(0, echoPercent) / 100m));
}

public static class RecoveryCalculator
{
    public static int Calculate(int maxHp, int flatAmount, decimal maxHpPercent) =>
        (int)Math.Min(int.MaxValue, decimal.Floor(Math.Max(0, flatAmount) + Math.Max(0, maxHp) * Math.Max(0, maxHpPercent) / 100m));
}

public static class ElementMatchup
{
    public static int PlayerAttackPercent(ElementType? player, ElementType monster)
    {
        if (player is null) return 0;
        if (IsLightDarkPair(player.Value, monster)) return 25;
        return DirectionalPercent(player.Value, monster);
    }

    public static int MonsterAttackPercent(ElementType monster, ElementType? player)
    {
        if (player is null) return 0;
        if (IsLightDarkPair(player.Value, monster)) return -25;
        return DirectionalPercent(monster, player.Value);
    }

    private static bool IsLightDarkPair(ElementType first, ElementType second) =>
        first is ElementType.Light && second is ElementType.Dark ||
        first is ElementType.Dark && second is ElementType.Light;

    private static int DirectionalPercent(ElementType attacker, ElementType defender) =>
        (attacker, defender) switch
        {
            (ElementType.Fire, ElementType.Wind) or
            (ElementType.Wind, ElementType.Earth) or
            (ElementType.Earth, ElementType.Water) or
            (ElementType.Water, ElementType.Fire) => 25,
            (ElementType.Wind, ElementType.Fire) or
            (ElementType.Earth, ElementType.Wind) or
            (ElementType.Water, ElementType.Earth) or
            (ElementType.Fire, ElementType.Water) => -25,
            _ => 0
        };
}
