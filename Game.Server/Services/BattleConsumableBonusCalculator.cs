using Game.Shared.Models;
using Game.Shared.Enums;

namespace Game.Server.Services;

public static class BattleConsumableBonusCalculator
{
    public static void Apply(Character character, WeaponSkillBonuses? bonuses)
    {
        decimal Percent(WeaponSkillEffectType effect) =>
            bonuses?.Percent(effect) ?? 0;
        character.TemporaryWeaponAttackBonusPercent = bonuses is null ? 0 :
            bonuses.AttackPercent - character.WeaponAttackBonusPercent;
        character.TemporaryWeaponHealthBonusPercent = bonuses is null ? 0 :
            bonuses.HealthPercent - character.WeaponHealthBonusPercent;
        character.TemporaryWeaponCriticalChancePercent = bonuses is null ? 0 :
            bonuses.CriticalChancePercent - character.WeaponCriticalChancePercent;
        character.TemporaryWeaponStaminaPercent = bonuses is null ? 0 :
            Percent(WeaponSkillEffectType.StaminaPercent) - character.WeaponStaminaPercent;
        character.TemporaryWeaponEnmityPercent = bonuses is null ? 0 :
            Percent(WeaponSkillEffectType.EnmityPercent) - character.WeaponEnmityPercent;
        character.TemporaryWeaponDoubleAttackChancePercent = bonuses is null ? 0 :
            Percent(WeaponSkillEffectType.DoubleAttackChancePercent) - character.WeaponDoubleAttackChancePercent;
        character.TemporaryWeaponNormalEchoPercent = bonuses is null ? 0 :
            Percent(WeaponSkillEffectType.NormalEchoPercent) - character.WeaponNormalEchoPercent;
        character.TemporaryWeaponSkillDamagePercent = bonuses is null ? 0 :
            Percent(WeaponSkillEffectType.SkillDamagePercent) - character.WeaponSkillDamagePercent;
    }
}
