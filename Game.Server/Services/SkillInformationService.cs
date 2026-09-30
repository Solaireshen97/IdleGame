using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;

namespace Game.Server.Services;

/// <summary>Projects the same compiled effects for loadouts, room actions and monster intentions.</summary>
public sealed class SkillInformationService(BattleStatusCatalog? statuses = null, ProfessionMechanicCatalog? mechanics = null)
{
    private readonly ProfessionMechanicDescription _mechanics = new(mechanics ?? ProfessionMechanicCatalog.Default);

    public string Description(BattleSkillDefinition skill, bool canChooseTarget = false) =>
        skill is CharacterSkillDefinition character && _mechanics.Supports(character)
            ? string.Join("；", Effects(skill, canChooseTarget).Select(effect => effect.Summary)) + "。" + ConditionalDescription(character) + _mechanics.Skill(character)
            : skill.Description;

    private string ConditionalDescription(CharacterSkillDefinition skill)
    {
        if (skill.ConditionalDamageBonusPercent <= 0) return string.Empty;
        var conditions = new List<string>();
        if (skill.RequiredTargetStatusCode is { } code)
            conditions.Add($"目标具有{statuses?.Find(code)?.Name ?? code}");
        if (skill.TargetHpBelowPercent is int threshold) conditions.Add($"目标生命不高于 {threshold}%");
        return (conditions.Count > 0 ? string.Join("且", conditions) + "时，" : "") +
            $"直接伤害额外提高 {skill.ConditionalDamageBonusPercent:0.##}%。";
    }

    public List<SkillEffectResponse> Effects(BattleSkillDefinition skill, bool canChooseTarget = false) =>
        skill.Effects.Select(effect => Describe(effect, skill is MonsterSkillDefinition, canChooseTarget)).ToList();

    private SkillEffectResponse Describe(BattleSkillEffect effect, bool monster, bool canChooseTarget)
    {
        var configuredStatus = statuses?.Find(effect.StatusCode);
        var status = configuredStatus is null ? null : configuredStatus with { Description = _mechanics.Status(configuredStatus) };
        var target = canChooseTarget && effect.TargetPolicy.AllowsSelection ? "本次所选队友" : effect.TargetPolicy.Selection switch
        {
            BattleTargetSelection.Self => "自身",
            BattleTargetSelection.Primary => "敌人",
            BattleTargetSelection.Front => monster ? "前排角色" : "前排队友",
            BattleTargetSelection.LowestHp => "生命值比例最低的队友",
            BattleTargetSelection.AllAlive => monster ? "全体存活角色" : "全体存活队友",
            BattleTargetSelection.AllOtherAlive => "其他存活队友",
            BattleTargetSelection.FirstDebuffed => "首个有负面状态的队友",
            _ => "目标"
        };
        var summary = effect.Kind switch
        {
            BattleEffectKind.Damage => $"对{target}造成 {effect.AttackPowerPercent:0.##}% 攻击伤害" +
                (effect.Power > 0 ? $"，另加 {effect.Power} 点威力" : ""),
            BattleEffectKind.Heal => $"为{target}恢复" +
                (effect.Power > 0 ? $"{effect.Power} 点" : "") +
                (effect.Power > 0 && effect.HealMaxHpPercent > 0 ? " + " : "") +
                (effect.HealMaxHpPercent > 0 ? $"目标最大生命值的 {effect.HealMaxHpPercent:0.##}%" : "") + "生命值",
            BattleEffectKind.Guard => $"使{target}在本回合获得 {effect.Power}% 直接伤害减免",
            BattleEffectKind.Cleanse => $"净化{target}的一个可移除负面状态",
            BattleEffectKind.Dispel => $"驱散{target}的一个可移除增益状态",
            BattleEffectKind.Interrupt => "打断敌人当前可打断技能",
            BattleEffectKind.CooldownReduction => $"使自身其他伤害技能的剩余冷却缩短 {effect.Power} 回合",
            BattleEffectKind.ApplyStatus => StatusSummary(effect, target, status),
            _ => $"对{target}产生技能效果"
        };
        return new()
        {
            Type = effect.Type, Target = effect.Target, Power = effect.Power,
            AttackPowerPercent = effect.AttackPowerPercent, HealMaxHpPercent = effect.HealMaxHpPercent,
            StatusCode = effect.StatusCode, DurationRounds = effect.DurationRounds,
            Summary = summary, StatusName = status?.Name, StatusDescription = status?.Description,
            StatusIsPositive = status?.IsPositive
        };
    }

    private static string StatusSummary(BattleSkillEffect effect, string target, BattleStatusDefinition? status)
    {
        var lifetime = status?.Lifetime ?? BattleStatusLifetime.Rounds;
        var duration = lifetime switch
        {
            BattleStatusLifetime.Rounds when effect.DurationRounds == 0 => "本回合有效",
            BattleStatusLifetime.Rounds => $"本回合及后续 {effect.DurationRounds} 回合有效",
            BattleStatusLifetime.UntilConsumed => "消耗后移除，挑战结束时清空",
            BattleStatusLifetime.Encounter => "当前目标死亡或挑战结束时清空",
            BattleStatusLifetime.Run => "本次挑战持续",
            _ => "本回合有效"
        };
        var result = $"对{target}施加{status?.Name ?? "状态效果"}，{duration}";
        if (status?.EffectType == "DamageOverTime")
            result += effect.AttackPowerPercent > 0
                ? $"；从下一回合起，每回合造成施放时攻击力 {effect.AttackPowerPercent:0.##}% 的伤害"
                : $"；从下一回合起，每回合每层造成 {Math.Abs(status.ValuePerStack):0.##} 点伤害";
        else if (status?.EffectType == "HealOverTime")
            result += effect.HealMaxHpPercent > 0
                ? $"；从下一回合起，每回合恢复目标最大生命值的 {effect.HealMaxHpPercent:0.##}%"
                : $"；从下一回合起，每回合每层恢复 {Math.Abs(status.ValuePerStack):0.##} 点生命值";
        else if (status is not null)
            result += $"；{status.Description}";
        if (status?.CounterKind == BattleStatusCounterKind.Charges)
            result += $"；初始 {status.InitialStacks} 次";
        return result;
    }
}
