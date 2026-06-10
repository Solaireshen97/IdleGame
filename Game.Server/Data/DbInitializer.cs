using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(GameDbContext dbContext)
    {
        await dbContext.Database.EnsureCreatedAsync();
    }
}
