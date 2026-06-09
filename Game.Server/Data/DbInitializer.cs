using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(GameDbContext dbContext)
    {
        await dbContext.Database.EnsureCreatedAsync();

        if (await dbContext.Players.AnyAsync())
        {
            return;
        }

        var player = new Player
        {
            Name = "TestPlayer"
        };

        dbContext.Players.Add(player);
        await dbContext.SaveChangesAsync();

        var character = new Character
        {
            PlayerId = player.Id,
            Name = "Knight",
            Hp = 100,
            MaxHp = 100,
            Attack = 20,
            Defense = 5
        };

        var monster = new Monster
        {
            Name = "Slime",
            Hp = 50,
            MaxHp = 50,
            Attack = 8,
            Defense = 2
        };

        dbContext.Characters.Add(character);
        dbContext.Monsters.Add(monster);
        await dbContext.SaveChangesAsync();

        dbContext.Rooms.Add(new Room
        {
            PlayerId = player.Id,
            CharacterId = character.Id,
            MonsterId = monster.Id,
            Status = RoomStatus.Idle
        });

        await dbContext.SaveChangesAsync();
    }
}
