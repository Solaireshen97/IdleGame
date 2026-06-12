using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(GameDbContext dbContext)
    {
        await dbContext.Database.EnsureCreatedAsync();
        await EnsureUserActiveCharacterColumnAsync(dbContext);
    }

    private static async Task EnsureUserActiveCharacterColumnAsync(GameDbContext dbContext)
    {
        var columnExists = await dbContext.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_table_info('Users') WHERE name = 'ActiveCharacterId'")
            .SingleAsync();

        if (columnExists == 0)
        {
            await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE Users ADD COLUMN ActiveCharacterId INTEGER NULL");
        }
    }
}
