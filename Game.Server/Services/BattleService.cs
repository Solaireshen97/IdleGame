using Game.Server.Data;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public class BattleService(GameDbContext dbContext, UserService userService)
{
    private static readonly TimeSpan RoundCooldown = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AutoRoundCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PreparationTimeout = TimeSpan.FromSeconds(30);

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
            room.PreparationStartedAtUtc = now;
            room.BattleEndedAtUtc = null;
        }
        else if (aliveSlots.Where(x => x.Slot.UserId == user.Id).All(x => x.Slot.IsConfirmed))
        {
            return (BuildResult(room, slots, monster, now, ["Your characters are already prepared."]), "AlreadyPrepared");
        }

        foreach (var entry in aliveSlots.Where(x => x.Slot.UserId == user.Id || IsSlotAuto(room, x))) entry.Slot.IsConfirmed = true;
        if (aliveSlots.All(x => x.Slot.IsConfirmed)) return await ExecutePreparedRoundAsync(room, slots, monster, now, []);
        return await SaveResultAsync(room, slots, monster, now, []);
    }

    public async Task<(BattleResult? Result, string? Error)> SyncAsync(int roomId, string? token)
    {
        var (room, slots, monster, _, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (null, error);
        var now = DateTime.UtcNow;
        var aliveSlots = slots!.Where(x => x.Character.Hp > 0).ToList();
        if (room!.Status == RoomStatus.Preparing)
        {
            if (room.PreparationStartedAtUtc is not null && now >= room.PreparationStartedAtUtc.Value.Add(PreparationTimeout))
            {
                var logs = new List<string>();
                foreach (var entry in aliveSlots.Where(x => !x.Slot.IsConfirmed))
                {
                    entry.Slot.IsTemporaryAuto = true;
                    entry.Slot.IsConfirmed = true;
                    logs.Add($"Slot {entry.Slot.SlotIndex} {entry.Character.Name} timed out and was temporarily set to Auto.");
                }
                logs.Add("Preparation timed out. The round starts automatically.");
                return await ExecutePreparedRoundAsync(room, slots, monster!, now, logs);
            }
            return (BuildResult(room, slots, monster!, now, []), null);
        }

        var allAliveMembersAuto = aliveSlots.Count > 0 && aliveSlots.All(x => IsSlotAuto(room, x));
        var autoRoundCanStart = room.Status == RoomStatus.NotStarted ||
            (room.Status == RoomStatus.Cooldown && room.NextRoundAvailableAtUtc <= now);
        if (autoRoundCanStart && aliveSlots.Count > 0 && monster!.Hp > 0 && allAliveMembersAuto)
        {
            room.Status = RoomStatus.Preparing;
            room.NextRoundAvailableAtUtc = null;
            room.PreparationStartedAtUtc = now;
            foreach (var entry in aliveSlots) entry.Slot.IsConfirmed = true;
            return await ExecutePreparedRoundAsync(room, slots, monster, now, ["All members are on Auto. The round starts automatically."]);
        }
        return (BuildResult(room, slots, monster!, now, []), null);
    }

    public async Task<(BattleResult? Result, string? Error)> SetSlotAutoAsync(int roomId, SetSlotAutoRequest request, string? token)
    {
        var (room, slots, monster, user, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (null, error);
        var entry = slots!.SingleOrDefault(x => x.Slot.SlotIndex == request.SlotIndex);
        if (entry is null || entry.Slot.UserId != user!.Id || entry.Character.Hp <= 0 || (entry.Slot.UserId == room!.OwnerUserId && !entry.Slot.IsMainControl)) return (null, "AutoConfigurationDenied");

        entry.Slot.IsAutoEnabled = request.IsAutoEnabled;
        if (room!.Status == RoomStatus.Preparing && request.IsAutoEnabled)
        {
            entry.Slot.IsConfirmed = true;
            if (slots.Where(x => x.Character.Hp > 0).All(x => x.Slot.IsConfirmed))
                return await ExecutePreparedRoundAsync(room, slots, monster!, DateTime.UtcNow, []);
        }

        return await SaveResultAsync(room, slots, monster!, DateTime.UtcNow, []);
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
        mainControl.Hp = Math.Min(mainControl.MaxHp, mainControl.Hp + amount);
        await dbContext.SaveChangesAsync();
        return (true, null);
    }

    private async Task<(BattleResult? Result, string? Error)> ExecutePreparedRoundAsync(Room room, List<SlotCharacter> slots, Monster monster, DateTime now, List<string> logs)
    {
        var aliveSlots = slots.Where(x => x.Character.Hp > 0).OrderBy(x => x.Slot.SlotIndex).ToList();
        if (aliveSlots.Count == 0 || aliveSlots.Any(x => !x.Slot.IsConfirmed)) return (null, "PreparationRequired");
        foreach (var entry in aliveSlots)
        {
            var damage = Math.Max(1, entry.Character.Attack - monster.Defense);
            monster.Hp = Math.Max(0, monster.Hp - damage);
            logs.Add($"Slot {entry.Slot.SlotIndex} {entry.Character.Name} attacks {monster.Name} for {damage} damage.");
            if (monster.Hp <= 0) { SetBattleOver(room, now); logs.Add($"{monster.Name} is defeated."); break; }
        }
        if (monster.Hp > 0)
        {
            var target = slots.Where(x => x.Character.Hp > 0).OrderBy(x => x.Slot.SlotIndex).FirstOrDefault();
            if (target is null) { SetBattleOver(room, now); logs.Add("All characters are defeated."); }
            else
            {
                var damage = Math.Max(1, monster.Attack - target.Character.Defense);
                target.Character.Hp = Math.Max(0, target.Character.Hp - damage);
                logs.Add($"{monster.Name} attacks Slot {target.Slot.SlotIndex} {target.Character.Name} for {damage} damage.");
                if (!slots.Any(x => x.Character.Hp > 0)) { SetBattleOver(room, now); logs.Add("All characters are defeated."); }
                else { room.Status = RoomStatus.Cooldown; room.NextRoundAvailableAtUtc = now.Add(aliveSlots.All(x => IsSlotAuto(room, x)) ? AutoRoundCooldown : RoundCooldown); room.BattleEndedAtUtc = null; }
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
    private static bool IsSlotAuto(Room room, SlotCharacter entry) => entry.Slot.IsAutoEnabled || (entry.Slot.UserId == room.OwnerUserId && !entry.Slot.IsMainControl);
    private static void ClearRoundState(Room room, IEnumerable<SlotCharacter> slots) { room.PreparationStartedAtUtc = null; foreach (var entry in slots) { entry.Slot.IsConfirmed = false; entry.Slot.IsTemporaryAuto = false; } }
    private static void SetBattleOver(Room room, DateTime now) { room.Status = RoomStatus.BattleOver; room.NextRoundAvailableAtUtc = null; room.PreparationStartedAtUtc = null; room.BattleEndedAtUtc = now; }
    private static BattleResult BuildResult(Room room, List<SlotCharacter> slots, Monster monster, DateTime now, List<string> logs) => new() { RoomId = room.Id, CharacterHp = slots.OrderBy(x => x.Slot.SlotIndex).FirstOrDefault()?.Character.Hp ?? 0, CharacterMaxHp = slots.OrderBy(x => x.Slot.SlotIndex).FirstOrDefault()?.Character.MaxHp ?? 0, MonsterHp = monster.Hp, MonsterMaxHp = monster.MaxHp, RoomStatus = room.Status, NextRoundAvailableAtUtc = room.NextRoundAvailableAtUtc, BattleEndedAtUtc = room.BattleEndedAtUtc, ServerTimeUtc = now, CanExecuteRound = room.Status == RoomStatus.Preparing && slots.Where(x => x.Character.Hp > 0).All(x => x.Slot.IsConfirmed) && monster.Hp > 0, IsVictory = monster.Hp <= 0, IsCharacterDead = !slots.Any(x => x.Character.Hp > 0), Logs = logs };

    private async Task<(Room? Room, List<SlotCharacter>? Slots, Monster? Monster, User? User, string? Error)> GetBattleContextAsync(int roomId, string? token)
    {
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null) return (null, null, null, null, "NotFound");
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (room, null, null, null, error);
        var slotRows = await dbContext.RoomSlots.Where(x => x.RoomId == roomId && x.CharacterId.HasValue).OrderBy(x => x.SlotIndex).ToListAsync();
        if (!slotRows.Any(x => x.UserId == user!.Id)) return (room, null, null, user, "NotInRoom");
        var ids = slotRows.Select(x => x.CharacterId!.Value).ToList();
        var characters = await dbContext.Characters.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        var slots = slotRows.Where(x => characters.ContainsKey(x.CharacterId!.Value)).Select(x => new SlotCharacter(x, characters[x.CharacterId!.Value])).ToList();
        var monster = await dbContext.Monsters.FindAsync(room.MonsterId);
        return monster is null ? (room, slots, null, user, "MonsterNotFound") : (room, slots, monster, user, null);
    }
    private sealed record SlotCharacter(RoomSlot Slot, Character Character);
}