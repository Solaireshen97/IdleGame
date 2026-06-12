using Game.Server.Data;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public class BattleService(GameDbContext dbContext, UserService userService)
{
    public async Task<(BattleResult? Result, string? Error)> ExecuteBattleAsync(int roomId, string? token)
    {
        var (room, _, character, monster, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null)
        {
            return (null, error);
        }

        var activeRoom = room!;
        var activeCharacter = character!;
        var activeMonster = monster!;
        var logs = new List<string>();

        if (activeCharacter.Hp <= 0)
        {
            logs.Add("Character is defeated and cannot battle.");
            return (new BattleResult
            {
                RoomId = activeRoom.Id,
                CharacterHp = activeCharacter.Hp,
                CharacterMaxHp = activeCharacter.MaxHp,
                MonsterHp = activeMonster.Hp,
                MonsterMaxHp = activeMonster.MaxHp,
                IsVictory = false,
                IsCharacterDead = true,
                Logs = logs
            }, null);
        }

        if (activeMonster.Hp <= 0)
        {
            logs.Add("Monster is already defeated. Please reset the room.");
            return (new BattleResult
            {
                RoomId = activeRoom.Id,
                CharacterHp = activeCharacter.Hp,
                CharacterMaxHp = activeCharacter.MaxHp,
                MonsterHp = activeMonster.Hp,
                MonsterMaxHp = activeMonster.MaxHp,
                IsVictory = true,
                IsCharacterDead = activeCharacter.Hp <= 0,
                Logs = logs
            }, null);
        }

        activeRoom.Status = RoomStatus.InBattle;

        var characterDamage = Math.Max(1, activeCharacter.Attack - activeMonster.Defense);
        activeMonster.Hp = Math.Max(0, activeMonster.Hp - characterDamage);
        logs.Add($"{activeCharacter.Name} attacks {activeMonster.Name} for {characterDamage} damage.");

        if (activeMonster.Hp <= 0)
        {
            logs.Add($"{activeMonster.Name} is defeated.");
        }
        else
        {
            var monsterDamage = Math.Max(1, activeMonster.Attack - activeCharacter.Defense);
            activeCharacter.Hp = Math.Max(0, activeCharacter.Hp - monsterDamage);
            logs.Add($"{activeMonster.Name} attacks {activeCharacter.Name} for {monsterDamage} damage.");
        }

        activeRoom.Status = RoomStatus.Idle;

        await dbContext.SaveChangesAsync();

        return (new BattleResult
        {
            RoomId = activeRoom.Id,
            CharacterHp = activeCharacter.Hp,
            CharacterMaxHp = activeCharacter.MaxHp,
            MonsterHp = activeMonster.Hp,
            MonsterMaxHp = activeMonster.MaxHp,
            IsVictory = activeMonster.Hp <= 0,
            IsCharacterDead = activeCharacter.Hp <= 0,
            Logs = logs
        }, null);
    }

    public async Task<(bool Success, string? Error)> ResetBattleAsync(int roomId, string? token)
    {
        var (room, _, _, monster, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null)
        {
            return (false, error);
        }

        var activeRoom = room!;
        var activeMonster = monster!;
        activeMonster.Hp = activeMonster.MaxHp;
        activeRoom.Status = RoomStatus.Idle;

        await dbContext.SaveChangesAsync();
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> HealCharacterAsync(int roomId, string? token, int amount = 10)
    {
        var (_, _, character, _, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null)
        {
            return (false, error);
        }

        var activeCharacter = character!;
        activeCharacter.Hp = Math.Min(activeCharacter.MaxHp, activeCharacter.Hp + amount);

        await dbContext.SaveChangesAsync();
        return (true, null);
    }

    private async Task<(Room? Room, RoomMember? Member, Character? Character, Monster? Monster, string? Error)> GetBattleContextAsync(
        int roomId,
        string? token,
        bool requireCharacter = true)
    {
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null)
        {
            return (null, null, null, null, "NotFound");
        }

        var (_, currentCharacter, error) = await userService.GetCurrentUserAndActiveCharacterAsync(token);
        if (error is not null)
        {
            return (room, null, null, null, error);
        }

        var member = await dbContext.RoomMembers.FirstOrDefaultAsync(x => x.RoomId == roomId && x.CharacterId == currentCharacter!.Id);
        if (member is null)
        {
            return (room, null, null, null, "NotInRoom");
        }

        var character = requireCharacter ? currentCharacter : null;

        var monster = await dbContext.Monsters.FirstOrDefaultAsync(x => x.Id == room.MonsterId);
        if (monster is null)
        {
            return (room, member, character, null, "MonsterNotFound");
        }

        return (room, member, character, monster, null);
    }
}
