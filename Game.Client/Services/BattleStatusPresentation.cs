using Game.Shared.Dtos;
using Game.Shared.Enums;

namespace Game.Client.Services;

public static class BattleStatusPresentation
{
    public static string Glyph(BattleStatusEffectResponse effect) => effect.Mechanic switch
    {
        BattleStatusMechanic.Guard or BattleStatusMechanic.GuardCounterPermission => "◆",
        BattleStatusMechanic.GuardCounterattack or BattleStatusMechanic.NextNativeDamage or BattleStatusMechanic.NormalAttackEcho => "⚔",
        BattleStatusMechanic.ShadowCharges => "✧",
        BattleStatusMechanic.HunterMark or BattleStatusMechanic.HunterEagleEye => "◎",
        BattleStatusMechanic.MageDisorder or BattleStatusMechanic.MageDomain => "✦",
        BattleStatusMechanic.NextNativeHeal or BattleStatusMechanic.Revelation => "+",
        _ => effect.EffectType switch
        { "DamageOverTime" => "☠", "HealOverTime" => "+", "OperationPotion" or "CombatConsumable" => "✦", _ => effect.IsPositive ? "↑" : "↓" }
    };
}
