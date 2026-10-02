using Game.Server.Configuration;
using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed partial class BattleRoundExecutor
{
    private async Task<Dictionary<int, OperationPotionBonuses>> ApplyOperationPotionsAsync(Room room,
        List<BattleParticipant> aliveSlots, List<string> logs)
    {
        var characterIds = aliveSlots.Select(entry => entry.Character.Id).ToList();
        var states = await dbContext.BattleOperationPotionStates.Where(state =>
                state.RoomId == room.Id && state.RunSequence == room.RunSequence &&
                characterIds.Contains(state.CharacterId))
            .ToDictionaryAsync(state => state.CharacterId);
        var equipped = await dbContext.CharacterConsumableSlots.Where(slot =>
                characterIds.Contains(slot.CharacterId) &&
                slot.SlotIndex == ConsumableRules.OperationPotionSlotIndex)
            .ToDictionaryAsync(slot => slot.CharacterId);
        var itemCodes = equipped.Values.Where(slot => slot.ItemCode is not null)
            .Select(slot => slot.ItemCode!).Distinct().ToList();
        var stocks = await dbContext.CharacterItemStacks.Where(stack =>
                characterIds.Contains(stack.CharacterId) && itemCodes.Contains(stack.ItemCode))
            .ToDictionaryAsync(stack => (stack.CharacterId, stack.ItemCode));

        foreach (var participant in aliveSlots)
        {
            var characterId = participant.Character.Id;
            if (states.ContainsKey(characterId)) continue;
            var state = new BattleOperationPotionState
            {
                RoomId = room.Id, RunSequence = room.RunSequence, CharacterId = characterId
            };
            if (equipped.TryGetValue(characterId, out var slot) &&
                consumableCatalog.FindItem(slot.ItemCode) is { Kind: "OperationPotion" } item)
            {
                var usable = participant.Character.Level >= (item.Tier - 1) * 10 + 1 &&
                    ConsumableRules.EffectScalePercent(item.Tier, participant.Character.Level) > 0;
                if (usable && stocks.TryGetValue((characterId, item.Code), out var stock) && stock.Quantity > 0)
                {
                    stock.Quantity--;
                    stock.Version++;
                    state.ItemCode = item.Code;
                    state.AttackPercent = ConsumableRules.ScaledPercent(item.AttackPercent, item.Tier, participant.Character.Level);
                    state.FinalDamagePercent = ConsumableRules.ScaledPercent(item.FinalDamagePercent, item.Tier, participant.Character.Level);
                    state.NormalAttackDamagePercent = ConsumableRules.ScaledPercent(item.NormalAttackDamagePercent, item.Tier, participant.Character.Level);
                    state.AreaDamageReductionPercent = ConsumableRules.ScaledPercent(item.AreaDamageReductionPercent, item.Tier, participant.Character.Level);
                    state.DamageTakenPercent = item.DamageTakenPercent;
                    var actor = BattleActor.ForCharacter(new(participant.Slot, participant.Character));
                    using var action = _events.ActionScope(actor, item.Code, item.Name, BattleActionKind.Consumable);
                    _events.Status(room, "Character", characterId, BattleConsumableStatus.Operation(state, consumableCatalog), BattleStatusChange.Added, 0, 1);
                    logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 使用 {item.Name}，{ConsumableCatalog.Description(item, participant.Character.Level)}。");
                }
                else
                {
                    logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 的 {item.Name} {(usable ? "库存不足" : "当前等级无法生效")}，本场跳过使用。");
                }
            }
            states[characterId] = state;
            dbContext.BattleOperationPotionStates.Add(state);
        }
        return states.ToDictionary(entry => entry.Key, entry => new OperationPotionBonuses(
            entry.Value.AttackPercent, entry.Value.FinalDamagePercent, entry.Value.DamageTakenPercent,
            entry.Value.NormalAttackDamagePercent, entry.Value.AreaDamageReductionPercent));
    }

    public async Task UpdateTemporaryWeaponBonusesAsync(Room room, IEnumerable<BattleParticipant> participants)
    {
        var entries = participants.ToList();
        if (entries.Count == 0) return;
        var ids = entries.Select(entry => entry.Character.Id).ToList();
        var active = room.Status == RoomStatus.BattleOver
            ? []
            : await dbContext.BattleConsumableBuffs.Where(buff => buff.RoomId == room.Id &&
                buff.RunSequence == room.RunSequence && buff.ExpiresAfterRound >= room.RoundNumber &&
                ids.Contains(buff.CharacterId)).ToListAsync();
        active = active.Concat(dbContext.BattleConsumableBuffs.Local.Where(buff =>
            dbContext.Entry(buff).State == EntityState.Added && buff.RoomId == room.Id &&
            buff.RunSequence == room.RunSequence && buff.ExpiresAfterRound >= room.RoundNumber &&
            ids.Contains(buff.CharacterId))).ToList();
        var weapons = weaponCatalog is null ? [] :
            await dbContext.CharacterWeapons.Include(weapon => weapon.Skills)
                .Where(weapon => ids.Contains(weapon.CharacterId) && weapon.EquippedSlotIndex != null).ToListAsync();
        foreach (var entry in entries)
        {
            var buffs = active.Where(buff => buff.CharacterId == entry.Character.Id).ToList();
            BattleWeaponBonusSnapshot temporary;
            if (buffs.Count == 0 || weaponCatalog is null)
            {
                temporary = weaponCatalog is null ? new() : BattleConsumableBonusCalculator.CalculateCombatEffects(
                    weaponCatalog.CalculateBonuses(weapons.Where(weapon => weapon.CharacterId == entry.Character.Id)));
            }
            else
            {
                var levels = buffs.GroupBy(buff => buff.WeaponSkillCode, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Sum(buff => buff.SkillLevel), StringComparer.OrdinalIgnoreCase);
                temporary = BattleConsumableBonusCalculator.Calculate(entry.Character,
                    weaponCatalog.CalculateBonuses(weapons.Where(weapon => weapon.CharacterId == entry.Character.Id), levels));
            }
            temporary.ApplyTo(entry.Character);
            entry.Character.Hp = Math.Min(entry.Character.Hp, TalentRules.EffectiveMaxHp(entry.Character));
        }
    }

    private async Task ApplyCombatBuffsAsync(Room room, List<BattleParticipant> aliveSlots, Monster monster, List<string> logs)
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
        var states = await ReadConsumableUsesAsync(room, ids);

        foreach (var participant in aliveSlots)
        {
            var character = participant.Character;
            if (!equipment.TryGetValue(character.Id, out var slot)) continue;
            slot = BattleAutoPolicyResolver.Consumable(participant.Slot, slot);
            var manual = (participant.Slot.PendingConsumableSlotMask & ConsumableRules.SlotMask(slot.SlotIndex)) != 0;
            var item = consumableCatalog.FindItem(slot.ItemCode);
            if (!manual && !slot.AutoUseEnabled) continue;
            states.TryGetValue(character.Id, out var state);
            var stock = stocks.SingleOrDefault(stack => stack.CharacterId == character.Id && stack.ItemCode == item?.Code);
            var cooldown = cooldowns.SingleOrDefault(entry => entry.CharacterId == character.Id &&
                string.Equals(entry.CooldownGroup, item?.CooldownGroup, StringComparison.OrdinalIgnoreCase));
            var unavailable = ConsumableUsePolicy.UnavailableReason(room, character, slot.SlotIndex, item,
                stock?.Quantity ?? 0, Math.Max(0, (cooldown?.ReadyAtRound ?? 0) - room.RoundNumber), 0,
                active.Any(buff => buff.CharacterId == character.Id && buff.WeaponSkillCode == item?.WeaponSkillCode), state?.BuffUsesUsed ?? 0);
            if (unavailable is not null)
            {
                if (manual) LogSkippedConsumable(participant, item?.Name, unavailable, logs);
                continue;
            }
            var skillLevel = ConsumableRules.ScaledSkillLevel(item!.WeaponSkillLevel, item.Tier, character.Level);
            if (skillLevel <= 0) continue;
            if (!manual && !await MeetsConsumableAutoConditionAsync(room, monster, participant, aliveSlots, slot, item)) continue;
            state ??= AddConsumableUses(room, character.Id, states);
            state.BuffUsesUsed++;
            state.Version++;
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
            var actor = BattleActor.ForCharacter(new(participant.Slot, character));
            using var action = _events.ActionScope(actor, item.Code, item.Name, BattleActionKind.Consumable);
            _events.Status(room, "Character", character.Id, BattleConsumableStatus.Combat(buff, consumableCatalog, room.RoundNumber), BattleStatusChange.Added, 0, 1);
            logs.Add($"{participant.Slot.SlotIndex}号位 {character.Name} 使用 {item.Name}，获得 {ConsumableCatalog.Description(item, character.Level)}。");
        }
    }

    private async Task ApplyCombatConsumablesAsync(Room room, List<BattleParticipant> aliveSlots, Monster monster, List<string> logs)
    {
        var ids = aliveSlots.Select(entry => entry.Character.Id).ToList();
        var equipment = await dbContext.CharacterConsumableSlots.Where(slot =>
            ids.Contains(slot.CharacterId) && slot.SlotIndex == ConsumableRules.HealingPotionSlotIndex)
            .ToDictionaryAsync(slot => slot.CharacterId);
        var stocks = await dbContext.CharacterItemStacks.Where(stack => ids.Contains(stack.CharacterId)).ToListAsync();
        var cooldowns = await dbContext.BattleConsumableCooldowns.Where(cooldown =>
            cooldown.RoomId == room.Id && ids.Contains(cooldown.CharacterId)).ToListAsync();
        var states = await ReadConsumableUsesAsync(room, ids);

        foreach (var participant in aliveSlots)
        {
            var character = participant.Character;
            if (!equipment.TryGetValue(character.Id, out var slot)) continue;
            slot = BattleAutoPolicyResolver.Consumable(participant.Slot, slot);
            var manual = (participant.Slot.PendingConsumableSlotMask & ConsumableRules.SlotMask(slot.SlotIndex)) != 0;
            var maxHp = TalentRules.EffectiveMaxHp(character);
            if (!manual && !slot.AutoUseEnabled) continue;
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
            if (!manual && !await MeetsConsumableAutoConditionAsync(room, monster, participant, aliveSlots, slot, item!)) continue;
            state ??= AddConsumableUses(room, character.Id, states);
            // The existing round SaveChanges commits health, stock, cooldown, quota and room version together.
            var hpBefore = character.Hp;
            character.Hp += healed;
            var actor = BattleActor.ForCharacter(new BattleParticipant(participant.Slot, character));
            using var action = _events.ActionScope(actor, item!.Code, item.Name, BattleActionKind.Consumable);
            _events.Hp(room, BattleEventKind.Heal, actor, actor, raw, healed, hpBefore);
            stock!.Quantity--;
            stock.Version++;
            state.UsesUsed++;
            state.Version++;
            SetConsumableCooldown(room, character.Id, item!.CooldownGroup, item.CooldownRounds, cooldown, cooldowns);
            logs.Add($"{participant.Slot.SlotIndex}号位 {character.Name} 使用 {item.Name}，恢复 {healed} 点生命值，本次副本剩余 {ConsumableRules.HealingPotionUsesPerRun - state.UsesUsed}/{ConsumableRules.HealingPotionUsesPerRun} 次。");
        }
    }

    private async Task CaptureConsumableExpiryAsync(Room room)
    {
        var terminal = room.Status == RoomStatus.BattleOver;
        var buffs = await dbContext.BattleConsumableBuffs.Where(buff => buff.RoomId == room.Id &&
            buff.RunSequence == room.RunSequence && buff.ExpiresAfterRound >= room.RoundNumber).ToListAsync();
        buffs.AddRange(dbContext.BattleConsumableBuffs.Local.Where(buff => buff.RoomId == room.Id &&
            buff.RunSequence == room.RunSequence && buff.ExpiresAfterRound >= room.RoundNumber && !buffs.Contains(buff)));
        foreach (var buff in buffs.Where(buff => terminal || buff.ExpiresAfterRound == room.RoundNumber))
            _events.Status(room, "Character", buff.CharacterId, BattleConsumableStatus.Combat(buff, consumableCatalog, room.RoundNumber),
                terminal ? BattleStatusChange.Removed : BattleStatusChange.Expired, 1, 0);
        if (!terminal) return;
        var operations = await dbContext.BattleOperationPotionStates.Where(state => state.RoomId == room.Id &&
            state.RunSequence == room.RunSequence && state.ItemCode != null).ToListAsync();
        operations.AddRange(dbContext.BattleOperationPotionStates.Local.Where(state => state.RoomId == room.Id &&
            state.RunSequence == room.RunSequence && state.ItemCode != null && !operations.Contains(state)));
        foreach (var state in operations)
            _events.Status(room, "Character", state.CharacterId, BattleConsumableStatus.Operation(state, consumableCatalog), BattleStatusChange.Removed, 1, 0);
    }

    private async Task<bool> MeetsConsumableAutoConditionAsync(Room room, Monster monster, BattleParticipant participant,
        List<BattleParticipant> participants, CharacterConsumableSlot slot, ConsumableItemOptions item) =>
        SkillBattlePolicy.MeetsAutoCondition(
            await _skillSnapshots.CaptureAutoAsync(room, monster, participant.Character.Id,
                participants.Select(entry => (entry.Slot, entry.Character))),
            slot.AutoConditionOverride ?? ConsumableCatalog.DefaultAutoCondition(item), slot.AutoHpThresholdPercent);

    private async Task<Dictionary<int, BattleHealingPotionState>> ReadConsumableUsesAsync(Room room, List<int> ids)
    {
        var states = await dbContext.BattleHealingPotionStates.Where(state => state.RoomId == room.Id &&
            state.RunSequence == room.RunSequence && ids.Contains(state.CharacterId)).ToDictionaryAsync(state => state.CharacterId);
        // Buffs and healing may both create/update the same quota row before this round is saved.
        foreach (var state in dbContext.BattleHealingPotionStates.Local.Where(state => state.RoomId == room.Id &&
            state.RunSequence == room.RunSequence && ids.Contains(state.CharacterId)))
            states[state.CharacterId] = state;
        return states;
    }

    private BattleHealingPotionState AddConsumableUses(Room room, int characterId, Dictionary<int, BattleHealingPotionState> states)
    {
        var state = new BattleHealingPotionState { RoomId = room.Id, RunSequence = room.RunSequence, CharacterId = characterId };
        states.Add(characterId, state);
        dbContext.BattleHealingPotionStates.Add(state);
        return state;
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

    private static void LogSkippedConsumable(BattleParticipant participant, string? name, string reason, List<string> logs)
    {
        var description = reason switch
        {
            "HealingPotionLimitReached" => "本次副本治疗药水次数已用完",
            "BuffPotionLimitReached" => "本次副本强化药剂次数已用完",
            "HpFull" => "生命已满", "CharacterDead" => "角色已阵亡", "OutOfStock" => "库存不足",
            "ConsumableCooldown" => "药水仍在冷却", "BuffAlreadyActive" => "增益仍在生效",
            "ConsumableIneffective" => "当前等级无法生效", "BattleOver" => "战斗已结束",
            _ => "补给配置无效"
        };
        logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 未使用 {name ?? "药水"}：{description}。");
    }
}
