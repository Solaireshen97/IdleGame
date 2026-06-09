using Game.Server.Data;
using Game.Shared.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public class RoomService(GameDbContext dbContext)
{
    public async Task<RoomStateResponse?> GetRoomStateAsync(int roomId)
    {
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null)
        {
            return null;
        }

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
}
