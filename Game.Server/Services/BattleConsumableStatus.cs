using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;

namespace Game.Server.Services;

// Consumables keep their stock/quota persistence while sharing the status presentation contract.
public static class BattleConsumableStatus
{
    public static BattleStatusSnapshot Operation(BattleOperationPotionState state, ConsumableCatalog catalog) => new()
    {
        Code = $"operation-potion:{state.ItemCode}", Name = catalog.FindItem(state.ItemCode)?.Name ?? "作战药剂",
        Description = ConsumableCatalog.ActiveOperationDescription(state), EffectType = "OperationPotion",
        IsPositive = true, Stacks = 1, Lifetime = BattleStatusLifetime.Run, CounterKind = BattleStatusCounterKind.None,
        ExpiresAfterRound = int.MaxValue, DurationText = "本次挑战持续", SourceActorType = "Character",
        SourceActorId = state.CharacterId, SourceSkillCode = state.ItemCode
    };

    public static BattleStatusSnapshot Combat(BattleConsumableBuff buff, ConsumableCatalog catalog, int round) => new()
    {
        Code = $"combat-consumable:{buff.ItemCode}", Name = catalog.FindItem(buff.ItemCode)?.Name ?? "战斗药剂",
        Description = $"{ConsumableCatalog.WeaponSkillName(buff.WeaponSkillCode)} Lv{buff.SkillLevel}", EffectType = "CombatConsumable",
        IsPositive = true, Stacks = 1, Lifetime = BattleStatusLifetime.Rounds, CounterKind = BattleStatusCounterKind.None,
        AppliedRound = buff.AppliedRound, ExpiresAfterRound = buff.ExpiresAfterRound,
        RemainingRounds = Math.Max(0, buff.ExpiresAfterRound - round + 1), DurationText = $"剩余 {Math.Max(0, buff.ExpiresAfterRound - round + 1)} 回合",
        SourceActorType = "Character", SourceActorId = buff.CharacterId, SourceSkillCode = buff.ItemCode
    };
}
