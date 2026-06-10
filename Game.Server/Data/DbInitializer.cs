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

        dbContext.Characters.Add(character);
        await dbContext.SaveChangesAsync();
    }
}
