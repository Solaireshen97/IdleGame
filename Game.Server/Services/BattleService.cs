using Game.Server.Data;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public class BattleService(GameDbContext dbContext, UserService userService)
{
    private static readonly TimeSpan RoundCooldown = TimeSpan.FromSeconds(10);

    public async Task<(BattleResult? Result, string? Error)> StartPreparationAsync(int roomId, string? token)
    {
        var (room, slots, monster, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (null, error);
        var now = DateTime.UtcNow;

        if (room!.Status == RoomStatus.BattleOver) return (BuildResult(room, slots!, monster!, now, ["Battle is over. Please reset the room."]), "BattleOver");
        if (room.Status == RoomStatus.Preparing) return (null, "AlreadyPreparing");
        if (room.Status == RoomStatus.Cooldown && room.NextRoundAvailableAtUtc > now) return (BuildResult(room, slots!, monster!, now, ["Round is on cooldown."]), "RoundCooldown");

        var aliveSlots = slots!.Where(x => x.Character.Hp > 0).ToList();
        if (monster!.Hp <= 0 || aliveSlots.Count == 0)
        {
            ClearConfirmations(slots);
            SetBattleOver(room, now);
            room.Version++;
            var save = await SaveAsync();
            return (save.Success ? BuildResult(room, slots, monster, now, [monster.Hp <= 0 ? "Monster is already defeated. Please reset the room." : "All characters are defeated and cannot battle."]) : null, save.Error ?? "BattleOver");
        }

        room.Status = RoomStatus.Preparing;
        room.NextRoundAvailableAtUtc = null;
        room.BattleEndedAtUtc = null;
        foreach (var entry in slots)
        {
            entry.Slot.IsConfirmed = entry.Character.Hp > 0;
        }

        return await ExecutePreparedRoundAsync(room, slots, monster, now);
    }

    public async Task<(BattleResult? Result, string? Error)> ExecuteRoundAsync(int roomId, string? token)
    {
        var (room, slots, monster, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (null, error);
        var now = DateTime.UtcNow;
        if (room!.Status == RoomStatus.BattleOver) return (BuildResult(room, slots!, monster!, now, ["Battle is over. Please reset the room."]), "BattleOver");
        if (room.Status != RoomStatus.Preparing || slots!.Where(x => x.Character.Hp > 0).Any(x => !x.Slot.IsConfirmed))
        {
            return (BuildResult(room, slots, monster!, now, ["Preparation is required before executing a round."]), "PreparationRequired");
        }

        return await ExecutePreparedRoundAsync(room, slots, monster!, now);
    }

    public Task<(BattleResult? Result, string? Error)> ExecuteBattleAsync(int roomId, string? token) => ExecuteRoundAsync(roomId, token);

    public async Task<(bool Success, string? Error)> ResetBattleAsync(int roomId, string? token)
    {
        var (room, slots, monster, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (false, error);
        monster!.Hp = monster.MaxHp;
        ClearConfirmations(slots!);
        room!.Status = RoomStatus.NotStarted;
        room.NextRoundAvailableAtUtc = null;
        room.BattleEndedAtUtc = null;
        room.Version++;
        return await SaveAsync();
    }

    public async Task<(bool Success, string? Error)> HealCharacterAsync(int roomId, string? token, int amount = 10)
    {
        var (_, slots, _, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (false, error);
        var mainControl = slots!.FirstOrDefault(x => x.Slot.IsMainControl)?.Character;
        if (mainControl is null) return (false, "NoCharacterInRoom");
        mainControl.Hp = Math.Min(mainControl.MaxHp, mainControl.Hp + amount);
        await dbContext.SaveChangesAsync();
        return (true, null);
    }

    private async Task<(BattleResult? Result, string? Error)> ExecutePreparedRoundAsync(Room room, List<SlotCharacter> slots, Monster monster, DateTime now)
    {
        var aliveSlots = slots.Where(x => x.Character.Hp > 0).OrderBy(x => x.Slot.SlotIndex).ToList();
        if (room.Status != RoomStatus.Preparing || aliveSlots.Count == 0 || aliveSlots.Any(x => !x.Slot.IsConfirmed))
        {
            return (null, "PreparationRequired");
        }

        if (monster.Hp <= 0)
        {
            ClearConfirmations(slots);
            SetBattleOver(room, now);
            room.Version++;
            var save = await SaveAsync();
            return (save.Success ? BuildResult(room, slots, monster, now, ["Monster is already defeated. Please reset the room."]) : null, save.Error ?? "BattleOver");
        }

        var logs = new List<string>();
        foreach (var entry in aliveSlots)
        {
            var damage = Math.Max(1, entry.Character.Attack - monster.Defense);
            monster.Hp = Math.Max(0, monster.Hp - damage);
            logs.Add($"Slot {entry.Slot.SlotIndex} {entry.Character.Name} attacks {monster.Name} for {damage} damage.");
            if (monster.Hp <= 0)
            {
                SetBattleOver(room, now);
                logs.Add($"{monster.Name} is defeated.");
                break;
            }
        }

        if (monster.Hp > 0)
        {
            var target = slots.Where(x => x.Character.Hp > 0).OrderBy(x => x.Slot.SlotIndex).FirstOrDefault();
            if (target is null)
            {
                SetBattleOver(room, now);
                logs.Add("All characters are defeated.");
            }
            else
            {
                var damage = Math.Max(1, monster.Attack - target.Character.Defense);
                target.Character.Hp = Math.Max(0, target.Character.Hp - damage);
                logs.Add($"{monster.Name} attacks Slot {target.Slot.SlotIndex} {target.Character.Name} for {damage} damage.");
                if (target.Character.Hp <= 0 && !slots.Any(x => x.Character.Hp > 0))
                {
                    SetBattleOver(room, now);
                    logs.Add("All characters are defeated.");
                }
                else
                {
                    room.Status = RoomStatus.Cooldown;
                    room.NextRoundAvailableAtUtc = now.Add(RoundCooldown);
                    room.BattleEndedAtUtc = null;
                }
            }
        }

        ClearConfirmations(slots);
        room.Version++;
        var saveResult = await SaveAsync();
        return (saveResult.Success ? BuildResult(room, slots, monster, now, logs) : null, saveResult.Error);
    }

    private async Task<(bool Success, string? Error)> SaveAsync()
    {
        try
        {
            await dbContext.SaveChangesAsync();
            return (true, null);
        }
        catch (DbUpdateConcurrencyException)
        {
            return (false, "ConcurrencyConflict");
        }
    }

    private static void ClearConfirmations(IEnumerable<SlotCharacter> slots)
    {
        foreach (var entry in slots) entry.Slot.IsConfirmed = false;
    }

    private static void SetBattleOver(Room room, DateTime now)
    {
        room.Status = RoomStatus.BattleOver;
        room.NextRoundAvailableAtUtc = null;
        room.BattleEndedAtUtc = now;
    }

    private static BattleResult BuildResult(Room room, List<SlotCharacter> slots, Monster monster, DateTime now, List<string> logs)
    {
        var displayed = slots.OrderBy(x => x.Slot.SlotIndex).FirstOrDefault()?.Character;
        return new BattleResult
        {
            RoomId = room.Id, CharacterHp = displayed?.Hp ?? 0, CharacterMaxHp = displayed?.MaxHp ?? 0,
            MonsterHp = monster.Hp, MonsterMaxHp = monster.MaxHp, RoomStatus = room.Status,
            NextRoundAvailableAtUtc = room.NextRoundAvailableAtUtc, BattleEndedAtUtc = room.BattleEndedAtUtc, ServerTimeUtc = now,
            CanExecuteRound = room.Status == RoomStatus.Preparing && slots.Any(x => x.Character.Hp > 0) && slots.Where(x => x.Character.Hp > 0).All(x => x.Slot.IsConfirmed) && monster.Hp > 0,
            IsVictory = monster.Hp <= 0, IsCharacterDead = !slots.Any(x => x.Character.Hp > 0), Logs = logs
        };
    }

    private async Task<(Room? Room, List<SlotCharacter>? Slots, Monster? Monster, string? Error)> GetBattleContextAsync(int roomId, string? token)
    {
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null) return (null, null, null, "NotFound");
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (room, null, null, error);
        if (room.OwnerUserId != user!.Id) return (room, null, null, "NotInRoom");
        var slotRows = await dbContext.RoomSlots.Where(x => x.RoomId == roomId && x.CharacterId.HasValue).OrderBy(x => x.SlotIndex).ToListAsync();
        var ids = slotRows.Select(x => x.CharacterId!.Value).ToList();
        var characters = await dbContext.Characters.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        var slots = slotRows.Where(x => characters.ContainsKey(x.CharacterId!.Value)).Select(x => new SlotCharacter(x, characters[x.CharacterId!.Value])).ToList();
        var monster = await dbContext.Monsters.FindAsync(room.MonsterId);
        return monster is null ? (room, slots, null, "MonsterNotFound") : (room, slots, monster, null);
    }

    private sealed record SlotCharacter(RoomSlot Slot, Character Character);
}