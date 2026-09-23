using Game.Server.Configuration;
using Game.Shared;
using Game.Shared.Models;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class ConsumableCatalog
{
    private readonly Dictionary<string, ConsumableItemOptions> _items;

    public ConsumableCatalog(IOptions<ConsumableOptions> options, WeaponCatalog? weapons = null)
    {
        var settings = options.Value;
        if (settings.Items.Count == 0) throw new InvalidOperationException("At least one consumable item must be configured.");

        _items = new Dictionary<string, ConsumableItemOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in settings.Items)
        {
            var operationPower = item.AttackPercent + item.FinalDamagePercent +
                item.NormalAttackDamagePercent + item.AreaDamageReductionPercent;
            if (string.IsNullOrWhiteSpace(item.Code) || string.IsNullOrWhiteSpace(item.Name) ||
                item.Kind is not ("Healing" or "OperationPotion" or "CombatBuff") || item.Tier < 1 ||
                item.HealAmount < 0 || item.CooldownRounds < 0 || item.DurationRounds < 0 ||
                item.HealMaxHpPercent is < 0 or > 100 ||
                item.AttackPercent is < 0 or > 100 || item.FinalDamagePercent is < 0 or > 100 ||
                item.DamageTakenPercent is < 0 or > 100 ||
                item.NormalAttackDamagePercent is < 0 or > 100 ||
                item.AreaDamageReductionPercent is < 0 or > 100 ||
                item.Kind == "Healing" && (string.IsNullOrWhiteSpace(item.CooldownGroup) ||
                    item.HealAmount == 0 && item.HealMaxHpPercent == 0 ||
                    operationPower != 0 || item.DamageTakenPercent != 0 ||
                    item.WeaponSkillLevel != 0 || item.DurationRounds != 0) ||
                item.Kind == "OperationPotion" && (operationPower == 0 ||
                    item.HealAmount != 0 || item.HealMaxHpPercent != 0 ||
                    item.CooldownRounds != 0 || item.DurationRounds != 0 ||
                    item.WeaponSkillLevel != 0) ||
                item.Kind == "CombatBuff" && (string.IsNullOrWhiteSpace(item.CooldownGroup) ||
                    string.IsNullOrWhiteSpace(item.WeaponSkillCode) ||
                    item.WeaponSkillLevel is < 1 or > 20 ||
                    item.DurationRounds < 1 || item.CooldownRounds <= item.DurationRounds ||
                    item.HealAmount != 0 || item.HealMaxHpPercent != 0 ||
                    operationPower != 0 || item.DamageTakenPercent != 0 ||
                    weapons is not null && weapons.FindSkill(item.WeaponSkillCode) is null) ||
                !_items.TryAdd(item.Code, item))
                throw new InvalidOperationException($"Invalid consumable item configuration: {item.Code}");
            item.CooldownGroup = item.CooldownGroup.Trim().ToLowerInvariant();
        }
    }

    public IReadOnlyCollection<ConsumableItemOptions> Items => _items.Values;

    public ConsumableItemOptions? FindItem(string? code) =>
        code is not null && _items.TryGetValue(code, out var item) ? item : null;

    public static int HealAmountFor(ConsumableItemOptions item, int maxHp, int characterLevel = 1) =>
        (int)decimal.Floor(RecoveryCalculator.Calculate(maxHp, item.HealAmount, item.HealMaxHpPercent) *
            ConsumableRules.EffectScalePercent(item.Tier, characterLevel) / 100m);

    public static string Description(ConsumableItemOptions item, int characterLevel)
    {
        var scale = ConsumableRules.EffectScalePercent(item.Tier, characterLevel);
        if (scale == 0) return "当前等级已无效果";
        if (item.Kind == "Healing")
            return $"恢复 {ConsumableRules.ScaledPercent(item.HealAmount, item.Tier, characterLevel)} HP＋最大生命的 {item.HealMaxHpPercent * scale / 100m:0.##}% · 冷却 {item.CooldownRounds} 回合";
        if (item.Kind == "CombatBuff")
            return $"{WeaponSkillName(item.WeaponSkillCode)} Lv{ConsumableRules.ScaledSkillLevel(item.WeaponSkillLevel, item.Tier, characterLevel)} · 持续 {item.DurationRounds} 回合 · 冷却 {item.CooldownRounds} 回合";
        var effects = new List<string>();
        if (item.AttackPercent > 0) effects.Add($"攻击 +{ConsumableRules.ScaledPercent(item.AttackPercent, item.Tier, characterLevel)}%");
        if (item.FinalDamagePercent > 0) effects.Add($"最终伤害 +{ConsumableRules.ScaledPercent(item.FinalDamagePercent, item.Tier, characterLevel)}%");
        if (item.NormalAttackDamagePercent > 0) effects.Add($"普通攻击伤害 +{ConsumableRules.ScaledPercent(item.NormalAttackDamagePercent, item.Tier, characterLevel)}%");
        if (item.AreaDamageReductionPercent > 0) effects.Add($"全体攻击承伤 -{ConsumableRules.ScaledPercent(item.AreaDamageReductionPercent, item.Tier, characterLevel)}%");
        if (item.DamageTakenPercent > 0) effects.Add($"自身承伤 +{item.DamageTakenPercent}%");
        return string.Join(" · ", effects) + " · 每场 1 瓶";
    }

    public static string WeaponSkillName(string? code) => code switch
    {
        "weapon-attack" => "攻击", "weapon-enmity" => "背水", "weapon-might" => "神威",
        "weapon-tactics" => "战技", _ => "武器技能"
    };

    public static string ActiveOperationDescription(BattleOperationPotionState state)
    {
        var effects = new List<string>();
        if (state.AttackPercent > 0) effects.Add($"攻击 +{state.AttackPercent}%");
        if (state.FinalDamagePercent > 0) effects.Add($"最终伤害 +{state.FinalDamagePercent}%");
        if (state.NormalAttackDamagePercent > 0) effects.Add($"普通攻击伤害 +{state.NormalAttackDamagePercent}%");
        if (state.AreaDamageReductionPercent > 0) effects.Add($"全体攻击承伤 -{state.AreaDamageReductionPercent}%");
        if (state.DamageTakenPercent > 0) effects.Add($"自身承伤 +{state.DamageTakenPercent}%");
        return string.Join(" · ", effects) + " · 本场有效";
    }
}
