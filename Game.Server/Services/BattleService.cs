using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public class BattleService(GameDbContext dbContext, UserService userService, ProgressionService progressionService, ConsumableCatalog consumableCatalog)
{
    private static readonly TimeSpan RoundCooldown = TimeSpan.FromSeconds(BattleRules.RoundCooldownSeconds);
    private static readonly TimeSpan AutoRoundCooldown = TimeSpan.FromSeconds(BattleRules.AutoRoundCooldownSeconds);
    private static readonly TimeSpan PreparationTimeout = TimeSpan.FromSeconds(BattleRules.PreparationTimeoutSeconds);

    public async Task<(BattleResult? Result, string? Error)> StartPreparationAsync(int roomId, string? token)
    {
        var (room, slots, monster, user, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (null, error);
        var now = DateTime.UtcNow;
        if (room!.Status == RoomStatus.BattleOver) return (BuildResult(room, slots!, monster!, now, ["Battle is over. Please reset the room."]), "BattleOver");
        if (room.Status == RoomStatus.Cooldown && room.NextRoundAvailableAtUtc > now) return (BuildResult(room, slots!, monster!, now, ["Round is on cooldown."]), "RoundCooldown");

        var aliveSlots = slots!.Where(x => x.Character.Hp > 0).ToList();
        if (monster!.Hp <= 0 || aliveSlots.Count == 0)
        {
            ClearRoundState(room, slots);
            SetBattleOver(room, now);
            return await SaveResultAsync(room, slots, monster, now, [monster.Hp <= 0 ? "Monster is already defeated. Please reset the room." : "All characters are defeated and cannot battle."]);
        }

        if (!aliveSlots.Any(x => x.Slot.UserId == user!.Id)) return (null, "NoOwnedAliveCharacters");
        if (room.Status != RoomStatus.Preparing)
        {
            room.Status = RoomStatus.Preparing;
            room.NextRoundAvailableAtUtc = null;
            room.RoundCooldownDurationSeconds = null;
            room.PreparationStartedAtUtc ??= now;
            room.BattleEndedAtUtc = null;
        }
        else if (aliveSlots.Where(x => x.Slot.UserId == user.Id).All(x => x.Slot.IsConfirmed))
        {
            return (BuildResult(room, slots, monster, now, ["Your characters are already prepared."]), "AlreadyPrepared");
        }

        var clearedUserIds = await GetClearedUserIdsAsync(room.DungeonId);
        foreach (var entry in aliveSlots.Where(x => x.Slot.UserId == user.Id || IsSlotAuto(room, x, clearedUserIds))) entry.Slot.IsConfirmed = true;
        if (aliveSlots.All(x => x.Slot.IsConfirmed)) return await ExecutePreparedRoundAsync(room, slots, monster, now, []);
        return await SaveResultAsync(room, slots, monster, now, []);
    }

    public async Task<(BattleResult? Result, string? Error)> SyncAsync(int roomId, string? token)
    {
        var (room, slots, monster, _, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (null, error);
        return await SyncCoreAsync(room!, slots!, monster!);
    }

    public async Task<(BattleResult? Result, string? Error)> SyncRoomAsync(int roomId)
    {
        var (room, slots, monster, error) = await GetRoomStateAsync(roomId);
        if (error is not null) return (null, error);
        return await SyncCoreAsync(room!, slots!, monster!);
    }

    private async Task<(BattleResult? Result, string? Error)> SyncCoreAsync(Room room, List<SlotCharacter> slots, Monster monster)
    {
        var now = DateTime.UtcNow;
        var restartedBattle = false;
        if (room.Status == RoomStatus.BattleOver && room.IsRepeatBattle && monster.Hp <= 0 &&
            room.BattleEndedAtUtc is DateTime endedAt && now >= endedAt.AddSeconds(BattleRules.RepeatBattleDelaySeconds))
        {
            monster.Hp = monster.MaxHp;
            foreach (var entry in slots) entry.Character.Hp = TalentRules.EffectiveMaxHp(entry.Character);
            await ResetConsumableCooldownsAsync(room.Id);
            ClearRoundState(room, slots);
            room.RoundNumber = 0;
            room.Status = RoomStatus.NotStarted;
            room.NextRoundAvailableAtUtc = null;
            room.RoundCooldownDurationSeconds = null;
            room.PreparationStartedAtUtc = room.IsPreparationTimeoutEnabled ? now : null;
            room.BattleEndedAtUtc = null;
            restartedBattle = true;
        }

        var aliveSlots = slots.Where(x => x.Character.Hp > 0).ToList();
        var clearedUserIds = await GetClearedUserIdsAsync(room.DungeonId);
        var allAliveMembersAuto = aliveSlots.Count > 0 && aliveSlots.All(x => IsSlotAuto(room, x, clearedUserIds));
        var stateChanged = restartedBattle;
        if (room.Status == RoomStatus.Cooldown && room.NextRoundAvailableAtUtc <= now && !allAliveMembersAuto)
        {
            room.Status = RoomStatus.NotStarted;
            room.NextRoundAvailableAtUtc = null;
            room.RoundCooldownDurationSeconds = null;
            room.PreparationStartedAtUtc = room.IsPreparationTimeoutEnabled ? now : null;
            stateChanged = true;
        }

        if ((room.Status == RoomStatus.Preparing || room.Status == RoomStatus.NotStarted && !allAliveMembersAuto) &&
            room.IsPreparationTimeoutEnabled && aliveSlots.Count > 0 && monster.Hp > 0)
        {
            if (room.PreparationStartedAtUtc is null)
            {
                room.PreparationStartedAtUtc = now;
                stateChanged = true;
            }
            if (now >= room.PreparationStartedAtUtc.Value.Add(PreparationTimeout))
            {
                var logs = new List<string>();
                foreach (var entry in aliveSlots.Where(x => !x.Slot.IsConfirmed))
                {
                    entry.Slot.IsTemporaryAuto = true;
                    entry.Slot.IsConfirmed = true;
                    logs.Add($"Slot {entry.Slot.SlotIndex} {entry.Character.Name} timed out and was temporarily set to Auto.");
                }
                logs.Add("Preparation timed out. The round starts automatically.");
                return await ExecutePreparedRoundAsync(room, slots, monster, now, logs);
            }
        }
        if (room.Status == RoomStatus.Preparing)
        {
            return stateChanged ? await SaveResultAsync(room, slots, monster, now, []) : (BuildResult(room, slots, monster, now, []), null);
        }
        if (room.Status == RoomStatus.NotStarted && !allAliveMembersAuto)
        {
            return stateChanged
                ? await SaveResultAsync(room, slots, monster, now, restartedBattle ? ["The next dungeon battle is ready. The party is at full HP."] : [])
                : (BuildResult(room, slots, monster, now, []), null);
        }

        var autoRoundCanStart = room.Status == RoomStatus.NotStarted ||
            (room.Status == RoomStatus.Cooldown && room.NextRoundAvailableAtUtc <= now);
        if (autoRoundCanStart && aliveSlots.Count > 0 && monster.Hp > 0 && allAliveMembersAuto)
        {
            room.Status = RoomStatus.Preparing;
            room.NextRoundAvailableAtUtc = null;
            room.PreparationStartedAtUtc = now;
            foreach (var entry in aliveSlots) entry.Slot.IsConfirmed = true;
            var logs = restartedBattle
                ? new List<string> { "The next dungeon battle begins with the party at full HP.", "All members are on Auto. The round starts automatically." }
                : new List<string> { "All members are on Auto. The round starts automatically." };
            return await ExecutePreparedRoundAsync(room, slots, monster, now, logs);
        }
        return restartedBattle
            ? await SaveResultAsync(room, slots, monster, now, ["The next dungeon battle is ready. The party is at full HP."])
            : (BuildResult(room, slots, monster, now, []), null);
    }

    public async Task<(BattleResult? Result, string? Error)> SetSlotAutoAsync(int roomId, SetSlotAutoRequest request, string? token)
    {
        var (room, slots, monster, user, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (null, error);
        var entry = slots!.SingleOrDefault(x => x.Slot.SlotIndex == request.SlotIndex);
        if (entry is null || entry.Slot.UserId != user!.Id || entry.Character.Hp <= 0 || (entry.Slot.UserId == room!.OwnerUserId && !entry.Slot.IsMainControl)) return (null, "AutoConfigurationDenied");
        if (request.IsAutoEnabled && !await dbContext.UserDungeonClears.AnyAsync(clear => clear.UserId == user.Id && clear.DungeonId == room!.DungeonId)) return (null, "AutoNotUnlocked");

        var clearedUserIds = !request.IsAutoEnabled ? await GetClearedUserIdsAsync(room!.DungeonId) : [];
        var wasAllAliveMembersAuto = !request.IsAutoEnabled && slots.Where(slot => slot.Character.Hp > 0)
            .All(slot => IsSlotAuto(room!, slot, clearedUserIds));
        entry.Slot.IsAutoEnabled = request.IsAutoEnabled;
        var now = DateTime.UtcNow;
        if (!request.IsAutoEnabled && room!.Status == RoomStatus.Cooldown && room.NextRoundAvailableAtUtc is DateTime deadline &&
            (room.RoundCooldownDurationSeconds == BattleRules.AutoRoundCooldownSeconds ||
                room.RoundCooldownDurationSeconds is null && wasAllAliveMembersAuto))
        {
            var manualDeadline = deadline - AutoRoundCooldown + RoundCooldown;
            room.RoundCooldownDurationSeconds = BattleRules.RoundCooldownSeconds;
            if (manualDeadline <= now)
            {
                room.Status = RoomStatus.NotStarted;
                room.NextRoundAvailableAtUtc = null;
                room.PreparationStartedAtUtc = room.IsPreparationTimeoutEnabled ? now : null;
            }
            else
            {
                room.NextRoundAvailableAtUtc = manualDeadline;
            }
        }
        if (room!.Status == RoomStatus.Preparing && request.IsAutoEnabled)
        {
            entry.Slot.IsConfirmed = true;
            if (slots.Where(x => x.Character.Hp > 0).All(x => x.Slot.IsConfirmed))
                return await ExecutePreparedRoundAsync(room, slots, monster!, now, []);
        }

        return await SaveResultAsync(room, slots, monster!, now, []);
    }

    public async Task<(bool Success, string? Error)> QueueConsumableAsync(QueueConsumableRequest request, string? token)
    {
        var (room, slots, monster, user, error) = await GetBattleContextAsync(request.RoomId, token);
        if (error is not null) return (false, error);
        if (room!.Status == RoomStatus.BattleOver || monster!.Hp <= 0) return (false, "BattleOver");

        var participant = slots!.SingleOrDefault(entry => entry.Character.Id == request.CharacterId);
        if (participant is null || participant.Slot.UserId != user!.Id) return (false, "NotCharacterOwner");
        if (participant.Character.Hp <= 0) return (false, "CharacterDead");
        if (request.ConsumableSlotIndex is int slotIndex)
        {
            if (slotIndex < 1 || slotIndex > ConsumableRules.SlotCount) return (false, "InvalidSlotIndex");
            var equipped = await dbContext.CharacterConsumableSlots.SingleOrDefaultAsync(
                slot => slot.CharacterId == participant.Character.Id && slot.SlotIndex == slotIndex);
            var item = consumableCatalog.FindItem(equipped?.ItemCode);
            if (item is null) return (false, "NoConsumableEquipped");
            if (participant.Character.Hp >= TalentRules.EffectiveMaxHp(participant.Character)) return (false, "HpFull");
            var stock = await dbContext.CharacterItemStacks.SingleOrDefaultAsync(
                stack => stack.CharacterId == participant.Character.Id && stack.ItemCode == item.Code);
            if (stock?.Quantity is not > 0) return (false, "OutOfStock");
            var cooldown = await dbContext.BattleConsumableCooldowns.SingleOrDefaultAsync(
                entry => entry.RoomId == room.Id && entry.CharacterId == participant.Character.Id &&
                    entry.CooldownGroup == item.CooldownGroup);
            if (cooldown?.ReadyAtRound > room.RoundNumber) return (false, "ConsumableCooldown");
        }

        participant.Slot.PendingConsumableSlotIndex = request.ConsumableSlotIndex;
        room.Version++;
        return await SaveAsync();
    }

    public async Task<(BattleResult? Result, string? Error)> ExecuteRoundAsync(int roomId, string? token)
    {
        var (room, slots, monster, _, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (null, error);
        var now = DateTime.UtcNow;
        if (room!.Status != RoomStatus.Preparing || slots!.Where(x => x.Character.Hp > 0).Any(x => !x.Slot.IsConfirmed)) return (BuildResult(room, slots, monster!, now, ["Preparation is required before executing a round."]), "PreparationRequired");
        return await ExecutePreparedRoundAsync(room, slots, monster!, now, []);
    }

    public Task<(BattleResult? Result, string? Error)> ExecuteBattleAsync(int roomId, string? token) => ExecuteRoundAsync(roomId, token);

    public async Task<(bool Success, string? Error)> ResetBattleAsync(int roomId, string? token)
    {
        var (room, slots, monster, user, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (false, error);
        if (room!.OwnerUserId != user!.Id) return (false, "NotOwner");
        if (room.Status != RoomStatus.BattleOver) return (false, "BattleNotOver");
        if (room.IsRepeatBattle && monster!.Hp <= 0) return (false, "RepeatBattlePending");
        monster!.Hp = monster.MaxHp;
        foreach (var entry in slots!) entry.Character.Hp = TalentRules.EffectiveMaxHp(entry.Character);
        await ResetConsumableCooldownsAsync(room.Id);
        ClearRoundState(room, slots);
        room.RoundNumber = 0;
        room.Status = RoomStatus.NotStarted;
        room.NextRoundAvailableAtUtc = null;
        room.RoundCooldownDurationSeconds = null;
        room.PreparationStartedAtUtc = room.IsPreparationTimeoutEnabled ? DateTime.UtcNow : null;
        room.BattleEndedAtUtc = null;
        room.Version++;
        return await SaveAsync();
    }

    private async Task<(BattleResult? Result, string? Error)> ExecutePreparedRoundAsync(Room room, List<SlotCharacter> slots, Monster monster, DateTime now, List<string> logs)
    {
        var aliveSlots = slots.Where(x => x.Character.Hp > 0).OrderBy(x => x.Slot.SlotIndex).ToList();
        if (aliveSlots.Count == 0 || aliveSlots.Any(x => !x.Slot.IsConfirmed)) return (null, "PreparationRequired");
        foreach (var entry in aliveSlots)
        {
            var damage = Math.Max(1, TalentRules.EffectiveAttack(entry.Character) - monster.Defense);
            monster.Hp = Math.Max(0, monster.Hp - damage);
            logs.Add($"Slot {entry.Slot.SlotIndex} {entry.Character.Name} attacks {monster.Name} for {damage} damage.");
            if (monster.Hp <= 0)
            {
                SetBattleOver(room, now);
                await RecordDungeonClearsAsync(room.DungeonId, slots.Select(slot => slot.Slot.UserId).OfType<int>().Distinct(), now);
                var dungeon = await dbContext.Dungeons.FindAsync(room.DungeonId);
                if (dungeon is null) return (null, "DungeonNotFound");
                var reward = progressionService.GetVictoryExperience(dungeon.Code);
                foreach (var participant in slots.DistinctBy(slot => slot.Character.Id))
                {
                    var gain = progressionService.AwardVictoryExperience(participant.Character, reward);
                    if (gain.ExperienceGained > 0)
                        logs.Add($"Slot {participant.Slot.SlotIndex} {participant.Character.Name} gains {gain.ExperienceGained} EXP.");
                    if (gain.LevelsGained > 0)
                        logs.Add($"Slot {participant.Slot.SlotIndex} {participant.Character.Name} reached Lv.{participant.Character.Level} and gained {gain.LevelsGained} talent point(s).");
                }
                await AwardVictoryConsumablesAsync(dungeon.Code, slots, logs);
                logs.Add($"{monster.Name} is defeated.");
                break;
            }
        }
        if (monster.Hp > 0)
        {
            await ApplyCombatConsumablesAsync(room, aliveSlots, logs);
            var target = slots.Where(x => x.Character.Hp > 0).OrderBy(x => x.Slot.SlotIndex).FirstOrDefault();
            if (target is null) { SetBattleOver(room, now); logs.Add("All characters are defeated."); }
            else
            {
                var damage = Math.Max(1, monster.Attack - TalentRules.EffectiveDefense(target.Character));
                target.Character.Hp = Math.Max(0, target.Character.Hp - damage);
                logs.Add($"{monster.Name} attacks Slot {target.Slot.SlotIndex} {target.Character.Name} for {damage} damage.");
                if (!slots.Any(x => x.Character.Hp > 0)) { SetBattleOver(room, now); logs.Add("All characters are defeated."); }
                else
                {
                    var clearedUserIds = await GetClearedUserIdsAsync(room.DungeonId);
                    room.Status = RoomStatus.Cooldown;
                    room.RoundCooldownDurationSeconds = aliveSlots.All(slot => IsSlotAuto(room, slot, clearedUserIds)) ? BattleRules.AutoRoundCooldownSeconds : BattleRules.RoundCooldownSeconds;
                    room.NextRoundAvailableAtUtc = now.AddSeconds(room.RoundCooldownDurationSeconds.Value);
                    room.BattleEndedAtUtc = null;
                }
            }
        }
        room.RoundNumber++;
        ClearRoundState(room, slots);
        return await SaveResultAsync(room, slots, monster, now, logs);
    }

    private async Task AwardVictoryConsumablesAsync(string dungeonCode, List<SlotCharacter> slots, List<string> logs)
    {
        var drops = consumableCatalog.GetVictoryDrops(dungeonCode);
        if (drops.Count == 0) return;
        var characterIds = slots.Select(slot => slot.Character.Id).Distinct().ToList();
        var stacks = await dbContext.CharacterItemStacks.Where(stack => characterIds.Contains(stack.CharacterId)).ToListAsync();
        foreach (var participant in slots.DistinctBy(slot => slot.Character.Id))
        {
            foreach (var drop in drops)
            {
                var stack = stacks.SingleOrDefault(item => item.CharacterId == participant.Character.Id && item.ItemCode == drop.ItemCode);
                if (stack is null)
                {
                    stack = new CharacterItemStack { CharacterId = participant.Character.Id, ItemCode = drop.ItemCode };
                    stacks.Add(stack);
                    dbContext.CharacterItemStacks.Add(stack);
                }
                else
                {
                    stack.Version++;
                }
                stack.Quantity = checked(stack.Quantity + drop.Quantity);
                var item = consumableCatalog.FindItem(drop.ItemCode)!;
                logs.Add($"Slot {participant.Slot.SlotIndex} {participant.Character.Name} receives {drop.Quantity} {item.Name}.");
            }
        }
    }

    private async Task ApplyCombatConsumablesAsync(Room room, List<SlotCharacter> aliveSlots, List<string> logs)
    {
        var characterIds = aliveSlots.Select(entry => entry.Character.Id).ToList();
        var equipment = await dbContext.CharacterConsumableSlots
            .Where(slot => characterIds.Contains(slot.CharacterId) && slot.ItemCode != null)
            .OrderBy(slot => slot.SlotIndex)
            .ToListAsync();
        if (equipment.Count == 0) return;
        var stocks = await dbContext.CharacterItemStacks
            .Where(stack => characterIds.Contains(stack.CharacterId))
            .ToListAsync();
        var cooldowns = await dbContext.BattleConsumableCooldowns
            .Where(cooldown => cooldown.RoomId == room.Id && characterIds.Contains(cooldown.CharacterId))
            .ToListAsync();

        foreach (var participant in aliveSlots)
        {
            var character = participant.Character;
            var characterEquipment = equipment.Where(slot => slot.CharacterId == character.Id).ToList();
            if (character.Hp >= TalentRules.EffectiveMaxHp(character)) continue;

            bool TryUse(CharacterConsumableSlot slot, bool automatic)
            {
                var item = consumableCatalog.FindItem(slot.ItemCode);
                if (item is null) return false;
                var maxHp = TalentRules.EffectiveMaxHp(character);
                if (automatic && (!slot.AutoUseEnabled || (long)character.Hp * 100 > (long)maxHp * slot.AutoHpThresholdPercent))
                    return false;
                var stock = stocks.SingleOrDefault(stack => stack.CharacterId == character.Id && stack.ItemCode == item.Code);
                if (stock?.Quantity is not > 0) return false;
                var cooldown = cooldowns.SingleOrDefault(entry => entry.CharacterId == character.Id &&
                    string.Equals(entry.CooldownGroup, item.CooldownGroup, StringComparison.OrdinalIgnoreCase));
                if (cooldown?.ReadyAtRound > room.RoundNumber) return false;

                var healed = Math.Min(item.HealAmount, maxHp - character.Hp);
                if (healed <= 0) return false;
                character.Hp += healed;
                stock.Quantity--;
                stock.Version++;
                if (cooldown is null)
                {
                    cooldown = new BattleConsumableCooldown
                    {
                        RoomId = room.Id,
                        CharacterId = character.Id,
                        CooldownGroup = item.CooldownGroup
                    };
                    cooldowns.Add(cooldown);
                    dbContext.BattleConsumableCooldowns.Add(cooldown);
                }
                cooldown.ReadyAtRound = checked(room.RoundNumber + item.CooldownRounds + 1);
                logs.Add($"Slot {participant.Slot.SlotIndex} {character.Name} uses {item.Name} and restores {healed} HP.");
                return true;
            }

            var selected = characterEquipment.SingleOrDefault(slot => slot.SlotIndex == participant.Slot.PendingConsumableSlotIndex);
            if (selected is not null && TryUse(selected, automatic: false)) continue;
            foreach (var slot in characterEquipment)
            {
                if (TryUse(slot, automatic: true)) break;
            }
        }
    }

    private async Task ResetConsumableCooldownsAsync(int roomId)
    {
        foreach (var cooldown in await dbContext.BattleConsumableCooldowns.Where(entry => entry.RoomId == roomId).ToListAsync())
            cooldown.ReadyAtRound = 0;
    }

    private async Task<(BattleResult? Result, string? Error)> SaveResultAsync(Room room, List<SlotCharacter> slots, Monster monster, DateTime now, List<string> logs)
    {
        room.Version++;
        var save = await SaveAsync();
        return (save.Success ? BuildResult(room, slots, monster, now, logs) : null, save.Error);
    }
    private async Task<(bool Success, string? Error)> SaveAsync()
    {
        dbContext.ChangeTracker.DetectChanges();
        foreach (var entry in dbContext.ChangeTracker.Entries<Character>().Where(entry => entry.State == EntityState.Modified))
            entry.Entity.Version++;
        try
        {
            await dbContext.SaveChangesAsync();
            return (true, null);
        }
        catch (DbUpdateException)
        {
            return (false, "ConcurrencyConflict");
        }
    }
    private static bool IsSlotAuto(Room room, SlotCharacter entry, List<int> clearedUserIds) =>
        entry.Slot.UserId.HasValue && clearedUserIds.Contains(entry.Slot.UserId.Value) &&
        (entry.Slot.IsAutoEnabled || (entry.Slot.UserId == room.OwnerUserId && !entry.Slot.IsMainControl));

    private Task<List<int>> GetClearedUserIdsAsync(int dungeonId) =>
        dbContext.UserDungeonClears.Where(clear => clear.DungeonId == dungeonId).Select(clear => clear.UserId).ToListAsync();

    private async Task RecordDungeonClearsAsync(int dungeonId, IEnumerable<int> userIds, DateTime clearedAtUtc)
    {
        var ids = userIds.Distinct().ToList();
        var existingIds = await dbContext.UserDungeonClears.Where(clear => clear.DungeonId == dungeonId && ids.Contains(clear.UserId)).Select(clear => clear.UserId).ToListAsync();
        dbContext.UserDungeonClears.AddRange(ids.Except(existingIds).Select(userId => new UserDungeonClear { UserId = userId, DungeonId = dungeonId, ClearedAtUtc = clearedAtUtc }));
    }
    private static void ClearRoundState(Room room, IEnumerable<SlotCharacter> slots) { room.PreparationStartedAtUtc = null; foreach (var entry in slots) { entry.Slot.IsConfirmed = false; entry.Slot.IsTemporaryAuto = false; entry.Slot.PendingConsumableSlotIndex = null; } }
    private static void SetBattleOver(Room room, DateTime now) { room.Status = RoomStatus.BattleOver; room.NextRoundAvailableAtUtc = null; room.RoundCooldownDurationSeconds = null; room.PreparationStartedAtUtc = null; room.BattleEndedAtUtc = now; }
    private static BattleResult BuildResult(Room room, List<SlotCharacter> slots, Monster monster, DateTime now, List<string> logs) => new() { RoomId = room.Id, CharacterHp = slots.OrderBy(x => x.Slot.SlotIndex).FirstOrDefault()?.Character.Hp ?? 0, CharacterMaxHp = slots.OrderBy(x => x.Slot.SlotIndex).Select(x => TalentRules.EffectiveMaxHp(x.Character)).FirstOrDefault(), MonsterHp = monster.Hp, MonsterMaxHp = monster.MaxHp, RoomStatus = room.Status, NextRoundAvailableAtUtc = room.NextRoundAvailableAtUtc, BattleEndedAtUtc = room.BattleEndedAtUtc, ServerTimeUtc = now, CanExecuteRound = room.Status == RoomStatus.Preparing && slots.Where(x => x.Character.Hp > 0).All(x => x.Slot.IsConfirmed) && monster.Hp > 0, IsVictory = monster.Hp <= 0, IsCharacterDead = !slots.Any(x => x.Character.Hp > 0), Logs = logs };

    private async Task<(Room? Room, List<SlotCharacter>? Slots, Monster? Monster, User? User, string? Error)> GetBattleContextAsync(int roomId, string? token)
    {
        var (room, slots, monster, roomError) = await GetRoomStateAsync(roomId);
        if (roomError is not null) return (room, slots, monster, null, roomError);
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (room, null, null, null, error);
        if (!slots!.Any(x => x.Slot.UserId == user!.Id)) return (room, null, null, user, "NotInRoom");
        return (room, slots, monster, user, null);
    }

    private async Task<(Room? Room, List<SlotCharacter>? Slots, Monster? Monster, string? Error)> GetRoomStateAsync(int roomId)
    {
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null) return (null, null, null, "NotFound");
        var slotRows = await dbContext.RoomSlots.Where(x => x.RoomId == roomId && x.CharacterId.HasValue).OrderBy(x => x.SlotIndex).ToListAsync();
        var ids = slotRows.Select(x => x.CharacterId!.Value).ToList();
        var characters = await dbContext.Characters.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        var slots = slotRows.Where(x => characters.ContainsKey(x.CharacterId!.Value)).Select(x => new SlotCharacter(x, characters[x.CharacterId!.Value])).ToList();
        var monster = await dbContext.Monsters.FindAsync(room.MonsterId);
        return monster is null ? (room, slots, null, "MonsterNotFound") : (room, slots, monster, null);
    }
    private sealed record SlotCharacter(RoomSlot Slot, Character Character);
}
