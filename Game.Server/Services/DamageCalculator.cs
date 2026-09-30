using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;

namespace Game.Server.Services;

// Each percentage belongs to one independent multiplier zone. Bonuses within a zone
// can be summed by the caller before the zones are multiplied together.
public readonly record struct DamageFactors(
    decimal AttackPercent = 0,
    decimal HealthPercent = 0,
    decimal CriticalPercent = 0,
    decimal ElementPercent = 0,
    decimal ReductionPercent = 0,
    decimal SkillDamagePercent = 0,
    decimal ConsumablePercent = 0);

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
        var consumableMultiplier = Math.Max(0m, 1m + factors.ConsumablePercent / 100m);

        // Skill power is flat attack. Defense is removed before the remaining zones.
        var afterDefense = Math.Max(1m, (attack * Math.Max(0, attackPowerPercent) / 100m + skillPower) * attackMultiplier - defense);
        var final = afterDefense * healthMultiplier * criticalMultiplier * elementMultiplier * reductionMultiplier * skillMultiplier * consumableMultiplier;
        return Math.Max(1, (int)Math.Min(int.MaxValue, decimal.Floor(final)));
    }
}

public static class WeaponCombatRules
{
    public static decimal AttackBonusPercent(Character character, int roundNumber) =>
        AttackBonusPercent(CharacterCombatStatSnapshot.Capture(character), roundNumber);

    public static decimal AttackBonusPercent(CharacterCombatStatSnapshot stats, int roundNumber) =>
        Math.Clamp(stats.AttackPercent + stats.RampAttackPerRoundPercent * Math.Min(10, Math.Max(1, roundNumber + 1)), 0m, 300m);

    public static decimal ElementAttackPercent(ElementType? attacker, ElementType defender, decimal advantagePercent)
    {
        var basePercent = ElementMatchup.PlayerAttackPercent(attacker, defender);
        return basePercent > 0 ? basePercent * (1m + Math.Clamp(advantagePercent, 0m, 100m) / 100m) : basePercent;
    }

    public static decimal WeaponReductionPercent(Character character)
        => WeaponReductionPercent(CharacterCombatStatSnapshot.Capture(character), character.Hp);

    public static decimal WeaponReductionPercent(CharacterCombatStatSnapshot stats, int hp)
    {
        var maxHp = stats.MaxHp;
        var lowHpFactor = maxHp <= 0 ? 0m : Math.Clamp((.5m - hp / (decimal)maxHp) / .5m, 0m, 1m);
        return Math.Clamp(stats.DirectReductionPercent + stats.LowHpReductionPercent * lowHpFactor, 0m, 50m);
    }

    public static decimal CombinedDirectReductionPercent(decimal otherReduction, Character character)
        => CombinedDirectReductionPercent(otherReduction, CharacterCombatStatSnapshot.Capture(character), character.Hp);

    public static decimal CombinedDirectReductionPercent(decimal otherReduction, CharacterCombatStatSnapshot stats, int hp)
    {
        var weapon = WeaponReductionPercent(stats, hp);
        var combined = 100m * (1m - (1m - otherReduction / 100m) * (1m - weapon / 100m));
        return Math.Min(BattleRules.MaxTotalDamageReductionPercent, combined);
    }

    public static decimal HealthDamagePercent(int hp, int maxHp, decimal stamina, decimal enmity)
    {
        if (hp <= 0 || maxHp <= 0) return 0;
        var ratio = Math.Clamp(hp / (decimal)maxHp, 0, 1);
        return stamina * Math.Clamp((ratio - .75m) / .25m, 0, 1) +
               enmity * Math.Clamp((.5m - ratio) / .5m, 0, 1);
    }

    public static bool RollPercent(decimal chance, Random? random = null)
    {
        var finalChance = Math.Clamp(chance, 0m, 100m);
        return finalChance >= 100m || finalChance > 0m &&
            (decimal)(random ?? Random.Shared).NextDouble() * 100m < finalChance;
    }

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
    public static int PlayerAttackPercent(ElementType? player, ElementType monster) =>
        Game.Shared.ElementMatchupRules.PlayerAttackPercent(player, monster);

    public static int MonsterAttackPercent(ElementType monster, ElementType? player) =>
        Game.Shared.ElementMatchupRules.MonsterAttackPercent(monster, player);
}
