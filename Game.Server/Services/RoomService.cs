using Game.Server.Data;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public class RoomService(GameDbContext dbContext)
{
    public async Task<List<RoomSummaryResponse>> GetRoomsAsync()
    {
        var rooms = await dbContext.Rooms.ToListAsync();
        var result = new List<RoomSummaryResponse>();

        foreach (var room in rooms)
        {
            var summary = await BuildRoomSummaryAsync(room);
            if (summary is not null)
            {
                result.Add(summary);
            }
        }

        return result;
    }

    public async Task<RoomStateResponse?> GetRoomStateAsync(int roomId)
    {
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null)
        {
            return null;
        }

        return await BuildRoomStateAsync(room);
    }

    public async Task<RoomSummaryResponse?> CreateRoomAsync(string monsterType)
    {
        var player = await dbContext.Players.FirstOrDefaultAsync();
        var character = await dbContext.Characters.FirstOrDefaultAsync();

        if (player is null || character is null)
        {
            return null;
        }

        var monster = CreateMonster(monsterType);

        dbContext.Monsters.Add(monster);
        await dbContext.SaveChangesAsync();

        var room = new Room
        {
            PlayerId = player.Id,
            CharacterId = character.Id,
            MonsterId = monster.Id,
            Status = RoomStatus.Idle
        };

        dbContext.Rooms.Add(room);
        await dbContext.SaveChangesAsync();

        return await BuildRoomSummaryAsync(room);
    }

    private static Monster CreateMonster(string monsterType)
    {
        return monsterType switch
        {
            "Goblin" => new Monster { Name = "Goblin", Hp = 80, MaxHp = 80, Attack = 12, Defense = 4 },
            "Wolf"   => new Monster { Name = "Wolf",   Hp = 65, MaxHp = 65, Attack = 15, Defense = 3 },
            _        => new Monster { Name = "Slime",  Hp = 50, MaxHp = 50, Attack = 8,  Defense = 2 }
        };
    }

    public async Task<(bool Success, string? Error)> DeleteRoomAsync(int roomId)
    {
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null)
        {
            return (false, "NotFound");
        }

        if (room.Status != RoomStatus.Idle)
        {
            return (false, "Room is currently in battle and cannot be deleted.");
        }

        var monster = await dbContext.Monsters.FirstOrDefaultAsync(x => x.Id == room.MonsterId);

        dbContext.Rooms.Remove(room);
        if (monster is not null)
        {
            dbContext.Monsters.Remove(monster);
        }

        await dbContext.SaveChangesAsync();
        return (true, null);
    }

    private async Task<RoomStateResponse?> BuildRoomStateAsync(Room room)
    {
        var player = await dbContext.Players.FirstOrDefaultAsync(x => x.Id == room.PlayerId);
        var character = await dbContext.Characters.FirstOrDefaultAsync(x => x.Id == room.CharacterId);
        var monster = await dbContext.Monsters.FirstOrDefaultAsync(x => x.Id == room.MonsterId);

        if (player is null || character is null || monster is null)
        {
            return null;
        }

        return new RoomStateResponse
        {
            RoomId = room.Id,
            PlayerName = player.Name,
            CharacterName = character.Name,
            CharacterHp = character.Hp,
            CharacterMaxHp = character.MaxHp,
            MonsterName = monster.Name,
            MonsterHp = monster.Hp,
            MonsterMaxHp = monster.MaxHp,
            RoomStatus = room.Status
        };
    }

    private async Task<RoomSummaryResponse?> BuildRoomSummaryAsync(Room room)
    {
        var monster = await dbContext.Monsters.FirstOrDefaultAsync(x => x.Id == room.MonsterId);

        if (monster is null)
        {
            return null;
        }

        return new RoomSummaryResponse
        {
            RoomId = room.Id,
            MonsterName = monster.Name,
            MonsterHp = monster.Hp,
            MonsterMaxHp = monster.MaxHp,
            RoomStatus = room.Status
        };
    }
}
