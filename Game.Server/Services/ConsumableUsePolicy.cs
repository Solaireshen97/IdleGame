using Game.Server.Configuration;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;

namespace Game.Server.Services;

public static class ConsumableUsePolicy
{
    public static string? UnavailableReason(Room room, Character character, int slotIndex,
        ConsumableItemOptions? item, int quantity, int cooldownRemaining, int healingUses, bool buffActive)
    {
        if (room.ClosedAtUtc.HasValue || room.Status == RoomStatus.BattleOver) return "BattleOver";
        if (character.Hp <= 0) return "CharacterDead";
        if (item is null) return "NoConsumableEquipped";
        if (!ConsumableRules.CanEquip(slotIndex, item.Kind)) return "WrongConsumableSlot";
        if (item.Kind == "Healing" && healingUses >= ConsumableRules.HealingPotionUsesPerRun)
            return "HealingPotionLimitReached";
        if (character.Level < (item.Tier - 1) * 10 + 1 ||
            ConsumableRules.EffectScalePercent(item.Tier, character.Level) == 0) return "ConsumableIneffective";
        if (item.Kind == "Healing" && character.Hp >= TalentRules.EffectiveMaxHp(character)) return "HpFull";
        if (item.Kind == "CombatBuff" && buffActive) return "BuffAlreadyActive";
        if (quantity <= 0) return "OutOfStock";
        if (cooldownRemaining > 0) return "ConsumableCooldown";
        return null;
    }
}
