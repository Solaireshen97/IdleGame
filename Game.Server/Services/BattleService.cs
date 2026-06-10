using Game.Server.Data;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public class BattleService(GameDbContext dbContext)
{
    public async Task<BattleResult?> ExecuteBattleAsync(int roomId)
    {
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null)
        {
            return null;
        }

        var character = await dbContext.Characters.FirstOrDefaultAsync(x => x.Id == room.CharacterId);
        var monster = await dbContext.Monsters.FirstOrDefaultAsync(x => x.Id == room.MonsterId);

        if (character is null || monster is null)
        {
            return null;
        }

        var logs = new List<string>();

        if (character.Hp <= 0)
        {
            logs.Add("Character is defeated and cannot battle.");
            return new BattleResult
            {
                RoomId = room.Id,
                CharacterHp = character.Hp,
                CharacterMaxHp = character.MaxHp,
                MonsterHp = monster.Hp,
                MonsterMaxHp = monster.MaxHp,
                IsVictory = false,
                IsCharacterDead = true,
                Logs = logs
            };
        }

        if (monster.Hp <= 0)
        {
            logs.Add("Monster is already defeated. Please reset the room.");
            return new BattleResult
            {
                RoomId = room.Id,
                CharacterHp = character.Hp,
                CharacterMaxHp = character.MaxHp,
                MonsterHp = monster.Hp,
                MonsterMaxHp = monster.MaxHp,
                IsVictory = true,
                IsCharacterDead = character.Hp <= 0,
                Logs = logs
            };
        }

        room.Status = RoomStatus.InBattle;

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

        return new BattleResult
        {
            RoomId = room.Id,
            CharacterHp = character.Hp,
            CharacterMaxHp = character.MaxHp,
            MonsterHp = monster.Hp,
            MonsterMaxHp = monster.MaxHp,
            IsVictory = monster.Hp <= 0,
            IsCharacterDead = character.Hp <= 0,
            Logs = logs
        };
    }
}
