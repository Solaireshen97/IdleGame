using Game.Shared.Enums;

namespace Game.Server.Services;

public sealed class ProfessionMechanicDescription(ProfessionMechanicCatalog catalog)
{
    public bool Supports(CharacterSkillDefinition skill) => !skill.IsShared &&
        skill.ProfessionCode is "swordsman" or "rogue" or "hunter" or "mage" or "acolyte";

    public string Skill(CharacterSkillDefinition skill)
    {
        if (!Supports(skill)) return string.Empty;
        var level = skill.Level;
        if (catalog.Knight.CanCounter(skill.Code))
            return $"受保护单位被怪物直接攻击后，骑士每回合最多以 {catalog.Knight.CounterAttackPowerPercent:0.##}% 攻击伤害反击一次。";
        if (catalog.Knight.DeduplicatesHealing(skill.Code)) return "同一目标只治疗一次。";
        if (catalog.Rogue.GeneratesCharge(skill.Code))
            return $"成功施放后获得 {catalog.Rogue.ChargeGain} 层影之蓄势。";
        if (skill.Code == "rogue-execution-slash")
            return $"消耗全部影之蓄势，每层使本次直接伤害提高 {catalog.Rogue.DamageBonusPerChargePercent:0.##}%；主伤害后目标存活且生命低于 {catalog.Rogue.ExecuteBelowHpPercent}%，追加主伤害实际值 {catalog.Rogue.ExecuteActualDamagePercent:0.##}% 的伤害。";
        if (skill.Code == "hunter-precision-shot")
            return $"持有当前怪物的猎物标记时，本次攻击倍率额外提高 {catalog.Hunter.MarkedPrecisionAttackBonusPercent.ForLevel(level):0.##} 个百分点；成功施放后消耗标记，鹰眼时刻可消耗 {catalog.Hunter.EagleEyeConsumption} 次次数保留标记。";
        if (skill.Code == "hunter-expose-shot")
            return $"持有当前怪物的猎物标记时，施加受到全部伤害提高 {catalog.Hunter.MarkedVulnerabilityPower.ForLevel(level):0.##}% 的破绽，本回合及后续 {catalog.Hunter.VulnerabilityDurationRounds} 回合有效；成功施放后消耗标记，鹰眼时刻可保留标记。";
        if (skill.Code == "hunter-hunting-signal")
            return $"持有当前怪物的猎物标记时，协猎追击比例提高至 {catalog.Hunter.MarkedCoordinatedPower.ForLevel(level):0.##} 个百分点；成功施放后消耗标记，鹰眼时刻可保留标记。";
        if (skill.ProfessionCode == "mage")
        {
            var result = $"成功施放本职技能获得 {catalog.Mage.DisorderGain} 层当前怪物的失序；每 {catalog.Mage.EchoRequiredStacks} 层触发 {catalog.Mage.EchoAttackPowerPercent:0.##}% 攻击伤害的回响，每回合最多一次，剩余层数保留；使怪物下次伤害性技能伤害降低 {catalog.Mage.BaseDisruptionPower:0.##}%。";
            if (skill.Code == "mage-arcane-domain")
                result += $"施放领域另获得 {catalog.Mage.DomainCastAdditionalDisorderGain} 层失序；领域的回响强化按领域状态生效。";
            return result;
        }
        if (skill.ProfessionCode == "acolyte")
        {
            var result = $"造成伤害后强化下一次本职治疗技能，治疗量提高 {catalog.Acolyte.HealingEnhancementPercent:0.##}%；成功治疗或净化后强化下一次本职伤害技能，伤害提高 {catalog.Acolyte.DamageEnhancementPercent:0.##}%。";
            if (skill.Code == "acolyte-purify" && level >= catalog.Acolyte.PurifySelfCleanseMinLevel)
                result += "另为自身净化一次，指定自身时不重复。";
            if (skill.Code == "acolyte-purify") result += "神启强化时为主目标额外净化一次。";
            if (skill.Code == "acolyte-revelation")
                result += $"接下来 {catalog.Acolyte.RevelationChargesByLevel.ForLevel(level)} 次本职技能固定获得强化。";
            return result;
        }
        return string.Empty;
    }

    public string Status(BattleStatusDefinition status, decimal? magnitude = null) => status.Mechanic switch
    {
        BattleStatusMechanic.ShadowCharges => $"最多 {status.MaxStacks} 层；斩击消耗全部层数，每层提高本次直接伤害 {catalog.Rogue.DamageBonusPerChargePercent:0.##}%。",
        BattleStatusMechanic.MageDisorder => $"当前怪物的失序每 {catalog.Mage.EchoRequiredStacks} 层触发一次回响，每回合最多一次。",
        BattleStatusMechanic.MageDomain => $"每回合获得 {catalog.Mage.DisorderGain} 层失序；回响造成 {catalog.Mage.DomainEchoAttackPowerPercent:0.##}% 攻击伤害，下次伤害性怪物技能减伤 {catalog.Mage.DisruptionPowerByDomainRank.ForLevel(status.MechanicLevel):0.##}%。",
        BattleStatusMechanic.NextNativeHeal => $"下一次本职治疗技能的治疗量提高 {catalog.Acolyte.HealingEnhancementPercent:0.##}%。",
        BattleStatusMechanic.NextNativeDamage => $"下一次本职伤害技能的伤害提高 {catalog.Acolyte.DamageEnhancementPercent:0.##}%。",
        BattleStatusMechanic.NextDamageSkillReduction => $"下一次造成直接伤害的非普通攻击技能伤害降低 {Math.Abs(magnitude ?? status.FamilyStrength):0.##}%。",
        BattleStatusMechanic.HunterCoordinated => $"普通攻击追击伤害比例提高 {Math.Abs(magnitude ?? status.FamilyStrength):0.##} 个百分点。",
        BattleStatusMechanic.HunterVulnerability => $"受到的所有伤害提高 {Math.Abs(magnitude ?? status.FamilyStrength):0.##}%。",
        _ => status.Description
    };
}
