using Game.Shared.Models;

namespace Game.Server.Services;

/// <summary>Temporary weapon deltas, separate from persistent equipment bonuses.</summary>
public sealed record BattleWeaponBonusSnapshot
{
    public decimal AttackPercent { get; init; }
    public decimal HealthPercent { get; init; }
    public decimal CriticalChancePercent { get; init; }
    public decimal StaminaPercent { get; init; }
    public decimal EnmityPercent { get; init; }
    public decimal DoubleAttackChancePercent { get; init; }
    public decimal NormalEchoPercent { get; init; }
    public decimal SkillDamagePercent { get; init; }
    public decimal DirectReductionPercent { get; init; }
    public decimal LowHpReductionPercent { get; init; }
    public decimal RampAttackPerRoundPercent { get; init; }
    public decimal ElementAdvantagePercent { get; init; }

    // Compatibility adapter while read models and older combat callers use the unmapped fields.
    public void ApplyTo(Character character)
    {
        character.TemporaryWeaponAttackBonusPercent = AttackPercent;
        character.TemporaryWeaponHealthBonusPercent = HealthPercent;
        character.TemporaryWeaponCriticalChancePercent = CriticalChancePercent;
        character.TemporaryWeaponStaminaPercent = StaminaPercent;
        character.TemporaryWeaponEnmityPercent = EnmityPercent;
        character.TemporaryWeaponDoubleAttackChancePercent = DoubleAttackChancePercent;
        character.TemporaryWeaponNormalEchoPercent = NormalEchoPercent;
        character.TemporaryWeaponSkillDamagePercent = SkillDamagePercent;
        character.CombatWeaponDirectReductionPercent = DirectReductionPercent;
        character.CombatWeaponLowHpReductionPercent = LowHpReductionPercent;
        character.CombatWeaponRampAttackPerRoundPercent = RampAttackPerRoundPercent;
        character.CombatWeaponElementAdvantagePercent = ElementAdvantagePercent;
    }
}
