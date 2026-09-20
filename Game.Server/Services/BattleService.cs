using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public class BattleService(GameDbContext dbContext, UserService userService, ProgressionService progressionService)
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
            ClearRoundState(room, slots);
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
        var (room, slots, monster, _, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (false, error);
        monster!.Hp = monster.MaxHp;
        ClearRoundState(room!, slots!);
        room.Status = RoomStatus.NotStarted;
        room.NextRoundAvailableAtUtc = null;
        room.RoundCooldownDurationSeconds = null;
        room.PreparationStartedAtUtc = room.IsPreparationTimeoutEnabled ? DateTime.UtcNow : null;
        room.BattleEndedAtUtc = null;
        room.Version++;
        return await SaveAsync();
    }

    public async Task<(bool Success, string? Error)> HealCharacterAsync(int roomId, string? token, int amount = 10)
    {
        var (_, slots, _, user, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (false, error);
        var mainControl = slots!.FirstOrDefault(x => x.Slot.IsMainControl && x.Slot.UserId == user!.Id)?.Character;
        if (mainControl is null) return (false, "NoCharacterInRoom");
        mainControl.Hp = Math.Min(TalentRules.EffectiveMaxHp(mainControl), mainControl.Hp + amount);
        await dbContext.SaveChangesAsync();
        return (true, null);
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
                logs.Add($"{monster.Name} is defeated.");
                break;
            }
        }
        if (monster.Hp > 0)
        {
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
        ClearRoundState(room, slots);
        return await SaveResultAsync(room, slots, monster, now, logs);
    }

    private async Task<(BattleResult? Result, string? Error)> SaveResultAsync(Room room, List<SlotCharacter> slots, Monster monster, DateTime now, List<string> logs)
    {
        room.Version++;
        var save = await SaveAsync();
        return (save.Success ? BuildResult(room, slots, monster, now, logs) : null, save.Error);
    }
    private async Task<(bool Success, string? Error)> SaveAsync() { try { await dbContext.SaveChangesAsync(); return (true, null); } catch (DbUpdateConcurrencyException) { return (false, "ConcurrencyConflict"); } }
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
    private static void ClearRoundState(Room room, IEnumerable<SlotCharacter> slots) { room.PreparationStartedAtUtc = null; foreach (var entry in slots) { entry.Slot.IsConfirmed = false; entry.Slot.IsTemporaryAuto = false; } }
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
