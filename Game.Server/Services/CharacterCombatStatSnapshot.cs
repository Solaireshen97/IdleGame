using Game.Shared;
using Game.Shared.Models;

namespace Game.Server.Services;

/// <summary>Immutable numerical inputs for one battle execution. Current HP stays on the actor.</summary>
public sealed record CharacterCombatStatSnapshot
{
    public int Attack { get; init; }
    public int MaxHp { get; init; }
    public decimal AttackPercent { get; init; }
    public decimal CriticalChancePercent { get; init; }
    public decimal StaminaPercent { get; init; }
    public decimal EnmityPercent { get; init; }
    public decimal DoubleAttackChancePercent { get; init; }
    public decimal NormalEchoPercent { get; init; }
    public decimal SkillDamagePercent { get; init; }
    public decimal SkillCriticalChancePercent { get; init; }
    public decimal NormalAttackPercent { get; init; }
    public decimal DirectReductionPercent { get; init; }
    public decimal LowHpReductionPercent { get; init; }
    public decimal RampAttackPerRoundPercent { get; init; }
    public decimal ElementAdvantagePercent { get; init; }
    public decimal HealingDonePercent { get; init; }
    public decimal HealingReceivedPercent { get; init; }

    public static CharacterCombatStatSnapshot Capture(Character character) => new()
    {
        Attack = TalentRules.EffectiveAttack(character), MaxHp = TalentRules.EffectiveMaxHp(character),
        AttackPercent = character.WeaponAttackBonusPercent + character.TemporaryWeaponAttackBonusPercent,
        CriticalChancePercent = character.WeaponCriticalChancePercent + character.TemporaryWeaponCriticalChancePercent,
        StaminaPercent = character.WeaponStaminaPercent + character.TemporaryWeaponStaminaPercent,
        EnmityPercent = character.WeaponEnmityPercent + character.TemporaryWeaponEnmityPercent,
        DoubleAttackChancePercent = character.WeaponDoubleAttackChancePercent + character.TemporaryWeaponDoubleAttackChancePercent,
        NormalEchoPercent = character.WeaponNormalEchoPercent + character.TemporaryWeaponNormalEchoPercent,
        SkillDamagePercent = character.WeaponSkillDamagePercent + character.TemporaryWeaponSkillDamagePercent + character.TalentSkillDamagePercent,
        SkillCriticalChancePercent = character.TalentSkillCriticalChancePercent,
        NormalAttackPercent = character.TalentNormalAttackPercent,
        DirectReductionPercent = character.CombatWeaponDirectReductionPercent,
        LowHpReductionPercent = character.CombatWeaponLowHpReductionPercent,
        RampAttackPerRoundPercent = character.CombatWeaponRampAttackPerRoundPercent,
        ElementAdvantagePercent = character.CombatWeaponElementAdvantagePercent,
        HealingDonePercent = character.TalentHealingDonePercent,
        HealingReceivedPercent = character.TalentHealingReceivedPercent
    };
}
