using Game.Server.Data;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public class BattleService(GameDbContext dbContext)
{
    public async Task<(BattleResult? Result, string? Error)> ExecuteBattleAsync(int roomId, string? token)
    {
        var (room, member, character, monster, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null)
        {
            return (null, error);
        }

        var logs = new List<string>();

        if (character.Hp <= 0)
        {
            logs.Add("Character is defeated and cannot battle.");
            return new BattleResult
            {
                RoomId = room!.Id,
                CharacterHp = character.Hp,
                CharacterMaxHp = character.MaxHp,
                MonsterHp = monster.Hp,
                MonsterMaxHp = monster.MaxHp,
                IsVictory = false,
                IsCharacterDead = true,
                Logs = logs
            }, null);
        }

        if (monster.Hp <= 0)
        {
            logs.Add("Monster is already defeated. Please reset the room.");
            return new BattleResult
            {
                RoomId = room!.Id,
                CharacterHp = character.Hp,
                CharacterMaxHp = character.MaxHp,
                MonsterHp = monster.Hp,
                MonsterMaxHp = monster.MaxHp,
                IsVictory = true,
                IsCharacterDead = character.Hp <= 0,
                Logs = logs
            }, null);
        }

        room!.Status = RoomStatus.InBattle;

        var characterDamage = Math.Max(1, character.Attack - monster.Defense);
        monster.Hp = Math.Max(0, monster.Hp - characterDamage);
        logs.Add($"{character.Name} attacks {monster.Name} for {characterDamage} damage.");

        if (monster.Hp <= 0)
        {
            logs.Add($"{monster.Name} is defeated.");
        }
        else
        {
            var monsterDamage = Math.Max(1, monster.Attack - character.Defense);
            character.Hp = Math.Max(0, character.Hp - monsterDamage);
            logs.Add($"{monster.Name} attacks {character.Name} for {monsterDamage} damage.");
        }

        room.Status = RoomStatus.Idle;

        await dbContext.SaveChangesAsync();

        return (new BattleResult
        {
            RoomId = room.Id,
            CharacterHp = character.Hp,
            CharacterMaxHp = character.MaxHp,
            MonsterHp = monster.Hp,
            MonsterMaxHp = monster.MaxHp,
            IsVictory = monster.Hp <= 0,
            IsCharacterDead = character.Hp <= 0,
            Logs = logs
        }, null);
    }

    public async Task<(bool Success, string? Error)> ResetBattleAsync(int roomId, string? token)
    {
        var (room, _, _, monster, error) = await GetBattleContextAsync(roomId, token, requireCharacter: false);
        if (error is not null)
        {
            return (false, error);
        }

        monster.Hp = monster.MaxHp;
        room.Status = RoomStatus.Idle;

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

        character.Hp = Math.Min(character.MaxHp, character.Hp + amount);

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

        var currentUser = await GetCurrentUserAsync(token);
        if (currentUser is null)
        {
            return (room, null, null, null, await GetCurrentUserErrorAsync(token));
        }

        var member = await dbContext.RoomMembers.FirstOrDefaultAsync(x => x.RoomId == roomId && x.UserId == currentUser.Id);
        if (member is null)
        {
            return (room, null, null, null, "NotInRoom");
        }

        Character? character = null;
        if (requireCharacter)
        {
            character = await dbContext.Characters.FirstOrDefaultAsync(x => x.Id == member.CharacterId);
            if (character is null)
            {
                return (room, member, null, null, "CharacterNotFound");
            }
        }

        var monster = await dbContext.Monsters.FirstOrDefaultAsync(x => x.Id == room.MonsterId);
        if (monster is null)
        {
            return (room, member, character, null, "MonsterNotFound");
        }

        return (room, member, character, monster, null);
    }

    private async Task<User?> GetCurrentUserAsync(string? token)
    {
        var session = await GetValidSessionAsync(token);
        if (session is null)
        {
            return null;
        }

        return await dbContext.Users.FirstOrDefaultAsync(x => x.Id == session.UserId);
    }

    private async Task<string> GetCurrentUserErrorAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "Unauthorized";
        }

        var session = await GetValidSessionAsync(token);
        if (session is null)
        {
            return "Unauthorized";
        }

        var userExists = await dbContext.Users.AnyAsync(x => x.Id == session.UserId);
        return userExists ? "Unauthorized" : "UserNotFound";
    }

    private async Task<UserLoginSession?> GetValidSessionAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var session = await dbContext.UserLoginSessions.FirstOrDefaultAsync(x => x.Token == token);
        if (session is null)
        {
            return null;
        }

        if (session.ExpireAt <= DateTime.UtcNow)
        {
            dbContext.UserLoginSessions.Remove(session);
            await dbContext.SaveChangesAsync();
            return null;
        }

        return session;
    }
}
