using Game.Server.Data;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public class RoomService(GameDbContext dbContext, UserService userService)
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

    public async Task<RoomDetailResponse?> GetRoomDetailAsync(int roomId, string? token = null)
    {
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null)
        {
            return null;
        }

        var currentUserId = await GetCurrentUserIdAsync(token);
        return await BuildRoomDetailAsync(room, currentUserId);
    }

    public async Task<(RoomDetailResponse? Detail, string? Error)> CreateRoomAsync(string monsterType, string? token)
    {
        var (user, character, error) = await GetCurrentUserAndCharacterAsync(token);

        if (error is not null)
        {
            return (null, error);
        }

        var existingMembership = await dbContext.RoomMembers.FirstOrDefaultAsync(x => x.UserId == user!.Id);
        if (existingMembership is not null)
        {
            return (null, "UserAlreadyInRoom");
        }

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

        var ownerMember = new RoomMember
        {
            RoomId = room.Id,
            UserId = user!.Id,
            CharacterId = character!.Id,
            IsOwner = true
        };

        dbContext.RoomMembers.Add(ownerMember);
        await dbContext.SaveChangesAsync();

        return (await BuildRoomDetailAsync(room, user.Id), null);
    }

    public async Task<(RoomDetailResponse? Detail, string? Error)> JoinRoomAsync(int roomId, string? token)
    {
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null)
        {
            return (null, "NotFound");
        }

        var (user, character, error) = await GetCurrentUserAndCharacterAsync(token);
        if (error is not null)
        {
            return (null, error);
        }

        var existingMembership = await dbContext.RoomMembers.FirstOrDefaultAsync(x => x.UserId == user!.Id);
        if (existingMembership is not null)
        {
            return (null, "UserAlreadyInRoom");
        }

        var existingMember = await dbContext.RoomMembers.FirstOrDefaultAsync(x => x.RoomId == roomId);
        if (existingMember is not null)
        {
            return (null, "RoomAlreadyHasMember");
        }

        var member = new RoomMember
        {
            RoomId = roomId,
            UserId = user!.Id,
            CharacterId = character!.Id
        };

        dbContext.RoomMembers.Add(member);
        await dbContext.SaveChangesAsync();

        return (await BuildRoomDetailAsync(room, user.Id), null);
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

    public async Task<(bool Success, string? Error)> DeleteRoomAsync(int roomId, string? token)
    {
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null)
        {
            return (false, "NotFound");
        }

        var currentUser = await GetCurrentUserAsync(token);
        if (currentUser is null)
        {
            return (false, await GetCurrentUserErrorAsync(token));
        }

        var ownerMember = await dbContext.RoomMembers
            .FirstOrDefaultAsync(x => x.RoomId == roomId && x.UserId == currentUser.Id && x.IsOwner);

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

    private async Task<RoomDetailResponse?> BuildRoomDetailAsync(Room room, int? currentUserId = null)
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

        var user = await dbContext.Users.FirstOrDefaultAsync(x => x.Id == member.UserId);
        var character = await dbContext.Characters.FirstOrDefaultAsync(x => x.Id == member.CharacterId);

        return new RoomDetailResponse
        {
            RoomId = room.Id,
            MonsterName = monster.Name,
            MonsterHp = monster.Hp,
            MonsterMaxHp = monster.MaxHp,
            RoomStatus = room.Status,
            HasPlayer = true,
            PlayerName = user?.UserName,
            CharacterName = character?.Name,
            CharacterHp = character?.Hp,
            CharacterMaxHp = character?.MaxHp,
            IsCurrentPlayerOwner = currentUserId.HasValue && member.IsOwner && member.UserId == currentUserId.Value
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

    private async Task<(User? User, Character? Character, string? Error)> GetCurrentUserAndCharacterAsync(string? token)
    {
        return await userService.GetCurrentUserAndActiveCharacterAsync(token);
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

    private async Task<int?> GetCurrentUserIdAsync(string? token)
    {
        var user = await GetCurrentUserAsync(token);
        return user?.Id;
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
