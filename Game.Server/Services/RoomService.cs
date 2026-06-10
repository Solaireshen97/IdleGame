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

    public async Task<RoomDetailResponse?> GetRoomDetailAsync(int roomId)
    {
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null)
        {
            return null;
        }

        return await BuildRoomDetailAsync(room);
    }

    public async Task<RoomDetailResponse?> CreateRoomAsync(string monsterType)
    {
        var monster = CreateMonster(monsterType);

        dbContext.Monsters.Add(monster);
        await dbContext.SaveChangesAsync();

        var room = new Room
        {
            MonsterId = monster.Id,
            Status = RoomStatus.Idle
        };

        dbContext.Rooms.Add(room);
        await dbContext.SaveChangesAsync();

        var player = await dbContext.Players.FirstOrDefaultAsync();
        var character = await dbContext.Characters.FirstOrDefaultAsync();

        if (player is null || character is null)
        {
            return null;
        }

        var ownerMember = new RoomMember
        {
            RoomId = room.Id,
            PlayerId = player.Id,
            CharacterId = character.Id,
            IsOwner = true
        };

        dbContext.RoomMembers.Add(ownerMember);
        await dbContext.SaveChangesAsync();

        return await BuildRoomDetailAsync(room);
    }

    public async Task<(RoomDetailResponse? Detail, string? Error)> JoinRoomAsync(int roomId)
    {
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null)
        {
            return (null, "NotFound");
        }

        var existingMember = await dbContext.RoomMembers.FirstOrDefaultAsync(x => x.RoomId == roomId);
        if (existingMember is not null)
        {
            return (null, "RoomAlreadyHasMember");
        }

        var player = await dbContext.Players.FirstOrDefaultAsync();
        var character = await dbContext.Characters.FirstOrDefaultAsync();

        if (player is null || character is null)
        {
            return (null, "DefaultPlayerOrCharacterNotFound");
        }

        var member = new RoomMember
        {
            RoomId = roomId,
            PlayerId = player.Id,
            CharacterId = character.Id
        };

        dbContext.RoomMembers.Add(member);
        await dbContext.SaveChangesAsync();

        return (await BuildRoomDetailAsync(room), null);
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

        var currentPlayer = await dbContext.Players.FirstOrDefaultAsync();
        if (currentPlayer is null)
        {
            return (false, "PlayerNotFound");
        }

        var ownerMember = await dbContext.RoomMembers
            .FirstOrDefaultAsync(x => x.RoomId == roomId && x.PlayerId == currentPlayer.Id && x.IsOwner);

        if (ownerMember is null)
        {
            return (false, "NotOwner");
        }

        var members = await dbContext.RoomMembers.Where(x => x.RoomId == roomId).ToListAsync();
        dbContext.RoomMembers.RemoveRange(members);

        var monster = await dbContext.Monsters.FirstOrDefaultAsync(x => x.Id == room.MonsterId);

        dbContext.Rooms.Remove(room);
        if (monster is not null)
        {
            dbContext.Monsters.Remove(monster);
        }

        await dbContext.SaveChangesAsync();
        return (true, null);
    }

    private async Task<RoomDetailResponse?> BuildRoomDetailAsync(Room room)
    {
        var monster = await dbContext.Monsters.FirstOrDefaultAsync(x => x.Id == room.MonsterId);
        if (monster is null)
        {
            return null;
        }

        var member = await dbContext.RoomMembers.FirstOrDefaultAsync(x => x.RoomId == room.Id);

        if (member is null)
        {
            return new RoomDetailResponse
            {
                RoomId = room.Id,
                MonsterName = monster.Name,
                MonsterHp = monster.Hp,
                MonsterMaxHp = monster.MaxHp,
                RoomStatus = room.Status,
                HasPlayer = false,
                IsCurrentPlayerOwner = false
            };
        }

        var player = await dbContext.Players.FirstOrDefaultAsync(x => x.Id == member.PlayerId);
        var character = await dbContext.Characters.FirstOrDefaultAsync(x => x.Id == member.CharacterId);

        return new RoomDetailResponse
        {
            RoomId = room.Id,
            MonsterName = monster.Name,
            MonsterHp = monster.Hp,
            MonsterMaxHp = monster.MaxHp,
            RoomStatus = room.Status,
            HasPlayer = true,
            PlayerName = player?.Name,
            CharacterName = character?.Name,
            CharacterHp = character?.Hp,
            CharacterMaxHp = character?.MaxHp,
            IsCurrentPlayerOwner = member.IsOwner
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

