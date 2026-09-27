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
            await UpdateTemporaryWeaponBonusesAsync(room, [participant]);
            var equipped = await dbContext.CharacterConsumableSlots.SingleOrDefaultAsync(
                slot => slot.CharacterId == participant.Character.Id && slot.SlotIndex == slotIndex);
            var item = consumableCatalog.FindItem(equipped?.ItemCode);
            var stock = item is null ? null : await dbContext.CharacterItemStacks.SingleOrDefaultAsync(
                stack => stack.CharacterId == participant.Character.Id && stack.ItemCode == item.Code);
            var cooldown = item is null ? null : await dbContext.BattleConsumableCooldowns.SingleOrDefaultAsync(
                entry => entry.RoomId == room.Id && entry.CharacterId == participant.Character.Id &&
                    entry.CooldownGroup == item.CooldownGroup);
            var healingUses = await dbContext.BattleHealingPotionStates
                .Where(state => state.RoomId == room.Id && state.RunSequence == room.RunSequence &&
                    state.CharacterId == participant.Character.Id).Select(state => (int?)state.UsesUsed).SingleOrDefaultAsync() ?? 0;
            var buffActive = item?.Kind == "CombatBuff" && await dbContext.BattleConsumableBuffs.AnyAsync(buff =>
                buff.RoomId == room.Id && buff.RunSequence == room.RunSequence &&
                buff.CharacterId == participant.Character.Id && buff.WeaponSkillCode == item.WeaponSkillCode &&
                buff.ExpiresAfterRound >= room.RoundNumber);
            var unavailable = ConsumableUsePolicy.UnavailableReason(room, participant.Character, slotIndex,
                item, stock?.Quantity ?? 0, Math.Max(0, (cooldown?.ReadyAtRound ?? 0) - room.RoundNumber),
                healingUses, buffActive);
            if (unavailable is not null) return (false, unavailable);
        }

        var mask = ConsumableRules.SlotMask(slotIndex);
        participant.Slot.PendingConsumableSlotMask = request.IsQueued
            ? participant.Slot.PendingConsumableSlotMask | mask
            : participant.Slot.PendingConsumableSlotMask & ~mask;
        room.Version++;
        return await SaveAsync();
    }

    private async Task ApplyCombatBuffsAsync(Room room, List<SlotCharacter> aliveSlots, List<string> logs)
    {
        if (weaponCatalog is null) return;
        var ids = aliveSlots.Select(entry => entry.Character.Id).ToList();
        var equipment = await dbContext.CharacterConsumableSlots.Where(slot =>
            ids.Contains(slot.CharacterId) && slot.SlotIndex == ConsumableRules.BuffPotionSlotIndex)
            .ToDictionaryAsync(slot => slot.CharacterId);
        var stocks = await dbContext.CharacterItemStacks.Where(stack => ids.Contains(stack.CharacterId)).ToListAsync();
        var cooldowns = await dbContext.BattleConsumableCooldowns.Where(cooldown =>
            cooldown.RoomId == room.Id && ids.Contains(cooldown.CharacterId)).ToListAsync();
        var active = await dbContext.BattleConsumableBuffs.Where(buff => buff.RoomId == room.Id &&
            buff.RunSequence == room.RunSequence && buff.ExpiresAfterRound >= room.RoundNumber &&
            ids.Contains(buff.CharacterId)).ToListAsync();

        foreach (var participant in aliveSlots)
        {
            var character = participant.Character;
            if (!equipment.TryGetValue(character.Id, out var slot)) continue;
            var manual = (participant.Slot.PendingConsumableSlotMask & ConsumableRules.SlotMask(slot.SlotIndex)) != 0;
            var item = consumableCatalog.FindItem(slot.ItemCode);
            if (!manual && (!slot.AutoUseEnabled || item is { WeaponSkillCode: "weapon-enmity" } &&
                (long)character.Hp * 100 > (long)TalentRules.EffectiveMaxHp(character) * 50)) continue;
            var stock = stocks.SingleOrDefault(stack => stack.CharacterId == character.Id && stack.ItemCode == item?.Code);
            var cooldown = cooldowns.SingleOrDefault(entry => entry.CharacterId == character.Id &&
                string.Equals(entry.CooldownGroup, item?.CooldownGroup, StringComparison.OrdinalIgnoreCase));
            var unavailable = ConsumableUsePolicy.UnavailableReason(room, character, slot.SlotIndex, item,
                stock?.Quantity ?? 0, Math.Max(0, (cooldown?.ReadyAtRound ?? 0) - room.RoundNumber), 0,
                active.Any(buff => buff.CharacterId == character.Id && buff.WeaponSkillCode == item?.WeaponSkillCode));
            if (unavailable is not null)
            {
                if (manual) LogSkippedConsumable(participant, item?.Name, unavailable, logs);
                continue;
            }
            var skillLevel = ConsumableRules.ScaledSkillLevel(item!.WeaponSkillLevel, item.Tier, character.Level);
            if (skillLevel <= 0) continue;
            stock!.Quantity--;
            stock.Version++;
            SetConsumableCooldown(room, character.Id, item.CooldownGroup, item.CooldownRounds, cooldown, cooldowns);
            var buff = new BattleConsumableBuff
            {
                RoomId = room.Id, RunSequence = room.RunSequence, CharacterId = character.Id,
                ItemCode = item.Code, WeaponSkillCode = item.WeaponSkillCode!, SkillLevel = skillLevel,
                AppliedRound = room.RoundNumber, ExpiresAfterRound = room.RoundNumber + item.DurationRounds - 1
            };
            active.Add(buff);
            dbContext.BattleConsumableBuffs.Add(buff);
            logs.Add($"{participant.Slot.SlotIndex}号位 {character.Name} 使用 {item.Name}，获得 {ConsumableCatalog.Description(item, character.Level)}。");
        }
    }

    private async Task ApplyCombatConsumablesAsync(Room room, List<SlotCharacter> aliveSlots, List<string> logs)
    {
        var ids = aliveSlots.Select(entry => entry.Character.Id).ToList();
        var equipment = await dbContext.CharacterConsumableSlots.Where(slot =>
            ids.Contains(slot.CharacterId) && slot.SlotIndex == ConsumableRules.HealingPotionSlotIndex)
            .ToDictionaryAsync(slot => slot.CharacterId);
        var stocks = await dbContext.CharacterItemStacks.Where(stack => ids.Contains(stack.CharacterId)).ToListAsync();
        var cooldowns = await dbContext.BattleConsumableCooldowns.Where(cooldown =>
            cooldown.RoomId == room.Id && ids.Contains(cooldown.CharacterId)).ToListAsync();
        var states = await dbContext.BattleHealingPotionStates.Where(state => state.RoomId == room.Id &&
            state.RunSequence == room.RunSequence && ids.Contains(state.CharacterId)).ToDictionaryAsync(state => state.CharacterId);

        foreach (var participant in aliveSlots)
        {
            var character = participant.Character;
            if (!equipment.TryGetValue(character.Id, out var slot)) continue;
            var manual = (participant.Slot.PendingConsumableSlotMask & ConsumableRules.SlotMask(slot.SlotIndex)) != 0;
            var maxHp = TalentRules.EffectiveMaxHp(character);
            if (!manual && (!slot.AutoUseEnabled || (long)character.Hp * 100 > (long)maxHp * slot.AutoHpThresholdPercent))
                continue;
            var item = consumableCatalog.FindItem(slot.ItemCode);
            var stock = stocks.SingleOrDefault(stack => stack.CharacterId == character.Id && stack.ItemCode == item?.Code);
            var cooldown = cooldowns.SingleOrDefault(entry => entry.CharacterId == character.Id &&
                string.Equals(entry.CooldownGroup, item?.CooldownGroup, StringComparison.OrdinalIgnoreCase));
            states.TryGetValue(character.Id, out var state);
            var unavailable = ConsumableUsePolicy.UnavailableReason(room, character, slot.SlotIndex, item,
                stock?.Quantity ?? 0, Math.Max(0, (cooldown?.ReadyAtRound ?? 0) - room.RoundNumber), state?.UsesUsed ?? 0, false);
            if (unavailable is not null)
            {
                if (manual) LogSkippedConsumable(participant, item?.Name, unavailable, logs);
                continue;
            }
            var raw = (int)decimal.Floor(ConsumableCatalog.HealAmountFor(item!, maxHp, character.Level) *
                (1 + character.TalentHealingReceivedPercent / 100m));
            var healed = Math.Min(raw, maxHp - character.Hp);
            if (healed <= 0) continue;

            if (state is null)
            {
                state = new BattleHealingPotionState { RoomId = room.Id, RunSequence = room.RunSequence, CharacterId = character.Id };
                states.Add(character.Id, state);
                dbContext.BattleHealingPotionStates.Add(state);
            }
            // The existing round SaveChanges commits health, stock, cooldown, quota and room version together.
            character.Hp += healed;
            stock!.Quantity--;
            stock.Version++;
            state.UsesUsed++;
            state.Version++;
            SetConsumableCooldown(room, character.Id, item!.CooldownGroup, item.CooldownRounds, cooldown, cooldowns);
            logs.Add($"{participant.Slot.SlotIndex}号位 {character.Name} 使用 {item.Name}，恢复 {healed} 点生命值，本次副本剩余 {ConsumableRules.HealingPotionUsesPerRun - state.UsesUsed}/{ConsumableRules.HealingPotionUsesPerRun} 次。");
        }
    }

    private void SetConsumableCooldown(Room room, int characterId, string group, int rounds,
        BattleConsumableCooldown? cooldown, List<BattleConsumableCooldown> cooldowns)
    {
        if (cooldown is null)
        {
            cooldown = new BattleConsumableCooldown { RoomId = room.Id, CharacterId = characterId, CooldownGroup = group };
            cooldowns.Add(cooldown);
            dbContext.BattleConsumableCooldowns.Add(cooldown);
        }
        cooldown.ReadyAtRound = checked(room.RoundNumber + rounds + 1);
    }

    private static void LogSkippedConsumable(SlotCharacter participant, string? name, string reason, List<string> logs)
    {
        var description = reason switch
        {
            "HealingPotionLimitReached" => "本次副本治疗药水次数已用完",
            "HpFull" => "生命已满", "CharacterDead" => "角色已阵亡", "OutOfStock" => "库存不足",
            "ConsumableCooldown" => "药水仍在冷却", "BuffAlreadyActive" => "增益仍在生效",
            "ConsumableIneffective" => "当前等级无法生效", "BattleOver" => "战斗已结束",
            _ => "补给配置无效"
        };
        logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 未使用 {name ?? "药水"}：{description}。");
    }
}
