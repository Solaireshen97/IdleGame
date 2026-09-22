using Game.Shared.Enums;

namespace Game.Shared;

public static class WeaponEffectLabels
{
    public static string Name(WeaponSkillEffectType effect) => effect switch
    {
        WeaponSkillEffectType.AttackPercent => "攻击",
        WeaponSkillEffectType.MaxHpPercent => "生命",
        WeaponSkillEffectType.CriticalChancePercent => "暴击率",
        WeaponSkillEffectType.StaminaPercent => "强壮",
        WeaponSkillEffectType.EnmityPercent => "背水",
        WeaponSkillEffectType.DoubleAttackChancePercent => "二连击率",
        WeaponSkillEffectType.NormalEchoPercent => "普攻追击",
        WeaponSkillEffectType.SkillDamagePercent => "技能伤害",
        _ => effect.ToString()
    };

    public static string Description(WeaponSkillEffectType effect) => effect switch
    {
        WeaponSkillEffectType.AttackPercent => "提高普通攻击与伤害技能的攻击力",
        WeaponSkillEffectType.MaxHpPercent => "提高武器提供的最大生命，不自动回复生命",
        WeaponSkillEffectType.CriticalChancePercent => "普通攻击与直接伤害技能可暴击，暴击伤害为150%",
        WeaponSkillEffectType.StaminaPercent => "生命高于75%时增伤，满生命获得完整收益",
        WeaponSkillEffectType.EnmityPercent => "生命低于50%时增伤，生命越低收益越高",
        WeaponSkillEffectType.DoubleAttackChancePercent => "普通攻击有概率多攻击一次，不重复使用技能或道具",
        WeaponSkillEffectType.NormalEchoPercent => "每次普攻命中后追加部分伤害，追击不会再次触发其他攻击",
        WeaponSkillEffectType.SkillDamagePercent => "提高职业技能的直接伤害，不影响治疗、道具或持续伤害",
        _ => ""
    };
}
