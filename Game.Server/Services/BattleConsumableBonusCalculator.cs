using Game.Shared.Models;
using Game.Shared.Enums;

namespace Game.Server.Services;

public static class BattleConsumableBonusCalculator
{
    public static void Apply(Character character, WeaponSkillBonuses? bonuses)
        => Calculate(character, bonuses).ApplyTo(character);

    public static BattleWeaponBonusSnapshot Calculate(Character character, WeaponSkillBonuses? bonuses)
    {
        decimal Percent(WeaponSkillEffectType effect) =>
            bonuses?.Percent(effect) ?? 0;
        return new()
        {
            AttackPercent = bonuses is null ? 0 : bonuses.AttackPercent - character.WeaponAttackBonusPercent,
            HealthPercent = bonuses is null ? 0 : bonuses.HealthPercent - character.WeaponHealthBonusPercent,
            CriticalChancePercent = bonuses is null ? 0 : bonuses.CriticalChancePercent - character.WeaponCriticalChancePercent,
            StaminaPercent = bonuses is null ? 0 : Percent(WeaponSkillEffectType.StaminaPercent) - character.WeaponStaminaPercent,
            EnmityPercent = bonuses is null ? 0 : Percent(WeaponSkillEffectType.EnmityPercent) - character.WeaponEnmityPercent,
            DoubleAttackChancePercent = bonuses is null ? 0 : Percent(WeaponSkillEffectType.DoubleAttackChancePercent) - character.WeaponDoubleAttackChancePercent,
            NormalEchoPercent = bonuses is null ? 0 : Percent(WeaponSkillEffectType.NormalEchoPercent) - character.WeaponNormalEchoPercent,
            SkillDamagePercent = bonuses is null ? 0 : Percent(WeaponSkillEffectType.SkillDamagePercent) - character.WeaponSkillDamagePercent,
            DirectReductionPercent = Percent(WeaponSkillEffectType.DirectDamageReductionPercent),
            LowHpReductionPercent = Percent(WeaponSkillEffectType.LowHpDamageReductionPercent),
            RampAttackPerRoundPercent = Percent(WeaponSkillEffectType.RampAttackPercent),
            ElementAdvantagePercent = Percent(WeaponSkillEffectType.ElementAdvantagePercent)
        };
    }

    public static void ApplyCombatEffects(Character character, WeaponSkillBonuses bonuses)
    {
        character.CombatWeaponDirectReductionPercent = bonuses.Percent(WeaponSkillEffectType.DirectDamageReductionPercent);
        character.CombatWeaponLowHpReductionPercent = bonuses.Percent(WeaponSkillEffectType.LowHpDamageReductionPercent);
        character.CombatWeaponRampAttackPerRoundPercent = bonuses.Percent(WeaponSkillEffectType.RampAttackPercent);
        character.CombatWeaponElementAdvantagePercent = bonuses.Percent(WeaponSkillEffectType.ElementAdvantagePercent);
    }

    public static BattleWeaponBonusSnapshot CalculateCombatEffects(WeaponSkillBonuses bonuses) => new()
    {
        DirectReductionPercent = bonuses.Percent(WeaponSkillEffectType.DirectDamageReductionPercent),
        LowHpReductionPercent = bonuses.Percent(WeaponSkillEffectType.LowHpDamageReductionPercent),
        RampAttackPerRoundPercent = bonuses.Percent(WeaponSkillEffectType.RampAttackPercent),
        ElementAdvantagePercent = bonuses.Percent(WeaponSkillEffectType.ElementAdvantagePercent)
    };
}
