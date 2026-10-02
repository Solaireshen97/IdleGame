using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public partial class BattleService
{
    public async Task<(bool Success, string? Error)> QueueConsumableAsync(QueueConsumableRequest request, string? token)
    {
        var (room, slots, monster, user, error) = await GetBattleContextAsync(request.RoomId, token);
        if (error is not null) return (false, error);
        if (request.ExpectedRunSequence != room!.RunSequence || request.ExpectedRoundNumber != room.RoundNumber)
            return (false, "StaleRound");
        if (room.Status == RoomStatus.BattleOver || monster!.Hp <= 0) return (false, "BattleOver");
        var participant = slots!.SingleOrDefault(entry => entry.Character.Id == request.CharacterId);
        if (participant is null || participant.Slot.UserId != user!.Id) return (false, "NotCharacterOwner");
        if (request.ConsumableSlotIndex is not int slotIndex || ConsumableRules.SlotMask(slotIndex) == 0)
            return (false, "InvalidSlotIndex");

        if (request.IsQueued)
        {
            // Rebuild temporary maxima on a fresh request, without granting any health.
            await Rounds.UpdateTemporaryWeaponBonusesAsync(room, [participant]);
            var equipped = await dbContext.CharacterConsumableSlots.SingleOrDefaultAsync(
                slot => slot.CharacterId == participant.Character.Id && slot.SlotIndex == slotIndex);
            var item = consumableCatalog.FindItem(equipped?.ItemCode);
            var stock = item is null ? null : await dbContext.CharacterItemStacks.SingleOrDefaultAsync(
                stack => stack.CharacterId == participant.Character.Id && stack.ItemCode == item.Code);
            var cooldown = item is null ? null : await dbContext.BattleConsumableCooldowns.SingleOrDefaultAsync(
                entry => entry.RoomId == room.Id && entry.CharacterId == participant.Character.Id &&
                    entry.CooldownGroup == item.CooldownGroup);
            var uses = await dbContext.BattleHealingPotionStates
                .Where(state => state.RoomId == room.Id && state.RunSequence == room.RunSequence &&
                    state.CharacterId == participant.Character.Id).SingleOrDefaultAsync();
            var buffActive = item?.Kind == "CombatBuff" && await dbContext.BattleConsumableBuffs.AnyAsync(buff =>
                buff.RoomId == room.Id && buff.RunSequence == room.RunSequence &&
                buff.CharacterId == participant.Character.Id && buff.WeaponSkillCode == item.WeaponSkillCode &&
                buff.ExpiresAfterRound >= room.RoundNumber);
            var unavailable = ConsumableUsePolicy.UnavailableReason(room, participant.Character, slotIndex,
                item, stock?.Quantity ?? 0, Math.Max(0, (cooldown?.ReadyAtRound ?? 0) - room.RoundNumber),
                uses?.UsesUsed ?? 0, buffActive, uses?.BuffUsesUsed ?? 0);
            if (unavailable is not null) return (false, unavailable);
        }

        var mask = ConsumableRules.SlotMask(slotIndex);
        participant.Slot.PendingConsumableSlotMask = request.IsQueued
            ? participant.Slot.PendingConsumableSlotMask | mask
            : participant.Slot.PendingConsumableSlotMask & ~mask;
        room.Version++;
        return await SaveAsync();
    }

}
