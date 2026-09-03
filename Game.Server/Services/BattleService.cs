using Game.Server.Data;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public class BattleService(GameDbContext dbContext, UserService userService)
{
    private static readonly TimeSpan RoundCooldown = TimeSpan.FromSeconds(10);

    public async Task<(BattleResult? Result, string? Error)> ExecuteRoundAsync(int roomId, string? token)
    {
        var (room, slots, monster, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (null, error);
        var now = DateTime.UtcNow;
        var activeRoom = room!;
        var activeMonster = monster!;
        var aliveSlots = slots!.Where(x => x.Character!.Hp > 0).OrderBy(x => x.Slot.SlotIndex).ToList();

        if (activeRoom.Status == RoomStatus.BattleOver) return (BuildResult(activeRoom, slots, activeMonster, now, ["Battle is over. Please reset the room."]), "BattleOver");
        if (activeRoom.NextRoundAvailableAtUtc > now) return (BuildResult(activeRoom, slots, activeMonster, now, ["Round is on cooldown."]), "RoundCooldown");

        if (activeMonster.Hp <= 0 || aliveSlots.Count == 0)
        {
            SetBattleOver(activeRoom, now);
            activeRoom.Version++;
            try { await dbContext.SaveChangesAsync(); } catch (DbUpdateConcurrencyException) { return (null, "ConcurrencyConflict"); }
            return (BuildResult(activeRoom, slots, activeMonster, now, [activeMonster.Hp <= 0 ? "Monster is already defeated. Please reset the room." : "All characters are defeated and cannot battle."]), "BattleOver");
        }

        var logs = new List<string>();
        foreach (var entry in aliveSlots)
        {
            var damage = Math.Max(1, entry.Character!.Attack - activeMonster.Defense);
            activeMonster.Hp = Math.Max(0, activeMonster.Hp - damage);
            logs.Add($"Slot {entry.Slot.SlotIndex} {entry.Character.Name} attacks {activeMonster.Name} for {damage} damage.");
            if (activeMonster.Hp <= 0)
            {
                SetBattleOver(activeRoom, now);
                logs.Add($"{activeMonster.Name} is defeated.");
                break;
            }
        }

        if (activeMonster.Hp > 0)
        {
            var target = slots.Where(x => x.Character!.Hp > 0).OrderBy(x => x.Slot.SlotIndex).FirstOrDefault();
            if (target is null)
            {
                SetBattleOver(activeRoom, now);
                logs.Add("All characters are defeated.");
            }
            else
            {
                var damage = Math.Max(1, activeMonster.Attack - target.Character!.Defense);
                target.Character.Hp = Math.Max(0, target.Character.Hp - damage);
                logs.Add($"{activeMonster.Name} attacks Slot {target.Slot.SlotIndex} {target.Character.Name} for {damage} damage.");
                if (target.Character.Hp <= 0 && !slots.Any(x => x.Character!.Hp > 0))
                {
                    SetBattleOver(activeRoom, now);
                    logs.Add("All characters are defeated.");
                }
                else
                {
                    activeRoom.Status = RoomStatus.Cooldown;
                    activeRoom.NextRoundAvailableAtUtc = now.Add(RoundCooldown);
                    activeRoom.BattleEndedAtUtc = null;
                }
            }
        }

        activeRoom.Version++;
        try { await dbContext.SaveChangesAsync(); } catch (DbUpdateConcurrencyException) { return (null, "ConcurrencyConflict"); }
        return (BuildResult(activeRoom, slots, activeMonster, now, logs), null);
    }

    public Task<(BattleResult? Result, string? Error)> ExecuteBattleAsync(int roomId, string? token) => ExecuteRoundAsync(roomId, token);

    public async Task<(bool Success, string? Error)> ResetBattleAsync(int roomId, string? token)
    {
        var (room, _, monster, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (false, error);
        monster!.Hp = monster.MaxHp;
        room!.Status = RoomStatus.NotStarted;
        room.NextRoundAvailableAtUtc = null;
        room.BattleEndedAtUtc = null;
        room.Version++;
        try { await dbContext.SaveChangesAsync(); } catch (DbUpdateConcurrencyException) { return (false, "ConcurrencyConflict"); }
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> HealCharacterAsync(int roomId, string? token, int amount = 10)
    {
        var (room, slots, _, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (false, error);
        var mainControl = slots!.FirstOrDefault(x => x.Slot.IsMainControl)?.Character;
        if (mainControl is null) return (false, "NoCharacterInRoom");
        mainControl.Hp = Math.Min(mainControl.MaxHp, mainControl.Hp + amount);
        await dbContext.SaveChangesAsync();
        return (true, null);
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
            CanExecuteRound = room.Status != RoomStatus.BattleOver && slots.Any(x => x.Character!.Hp > 0) && monster.Hp > 0 && (!room.NextRoundAvailableAtUtc.HasValue || room.NextRoundAvailableAtUtc <= now),
            IsVictory = monster.Hp <= 0, IsCharacterDead = !slots.Any(x => x.Character!.Hp > 0), Logs = logs
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