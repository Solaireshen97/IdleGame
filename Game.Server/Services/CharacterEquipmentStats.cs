using Game.Shared.Enums;
using Game.Shared.Models;

namespace Game.Server.Services;

/// <summary>Persistent loadout values; HP and concurrency versions remain owned by the command.</summary>
public sealed record CharacterEquipmentStats(int Attack, int MaxHp, WeaponSkillBonuses Bonuses)
{
    public static CharacterEquipmentStats Calculate(IEnumerable<CharacterWeapon> weapons, WeaponCatalog catalog)
    {
        var loadout = weapons.ToList();
        var equipped = loadout.Where(weapon => weapon.EquippedSlotIndex.HasValue).ToList();
        return new(equipped.Sum(weapon => weapon.Attack), equipped.Sum(weapon => weapon.MaxHp),
            catalog.CalculateBonuses(loadout));
    }

    public void ApplyTo(Character character)
    {
        character.Attack = Attack;
        character.MaxHp = MaxHp;
        ApplyBonusesTo(character, Bonuses);
    }

    internal static void ApplyBonusesTo(Character character, WeaponSkillBonuses bonuses)
    {
        character.WeaponAttackBonusPercent = bonuses.AttackPercent;
        character.WeaponHealthBonusPercent = bonuses.HealthPercent;
        character.WeaponCriticalChancePercent = bonuses.CriticalChancePercent;
        character.WeaponStaminaPercent = bonuses.Percent(WeaponSkillEffectType.StaminaPercent);
        character.WeaponEnmityPercent = bonuses.Percent(WeaponSkillEffectType.EnmityPercent);
        character.WeaponDoubleAttackChancePercent = bonuses.Percent(WeaponSkillEffectType.DoubleAttackChancePercent);
        character.WeaponNormalEchoPercent = bonuses.Percent(WeaponSkillEffectType.NormalEchoPercent);
        character.WeaponSkillDamagePercent = bonuses.Percent(WeaponSkillEffectType.SkillDamagePercent);
    }
}
