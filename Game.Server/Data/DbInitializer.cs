using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Game.Shared.Models;
using Game.Shared.Enums;

namespace Game.Server.Data;

public static class DbInitializer
{
    private const string InitialCreateMigrationId = "20260612043042_InitialCreate";
    private const string AddActiveCharacterMigrationId = "20260612044400_AddActiveCharacterId";

    public static async Task InitializeAsync(GameDbContext dbContext)
    {
        await AdoptLegacyEnsureCreatedDatabaseAsync(dbContext);
        await dbContext.Database.MigrateAsync();
        await EnsureDefaultDungeonsAsync(dbContext);
    }

    public static async Task EnsureDefaultDungeonsAsync(GameDbContext dbContext)
    {
        var existingCodes = await dbContext.Dungeons.Select(dungeon => dungeon.Code).ToListAsync();
        var defaults = new[]
        {
            new Dungeon { Code = "slime-field", Name = "史莱姆平原", MonsterName = "Slime", MonsterElement = ElementType.Wind, MonsterMaxHp = 50, MonsterAttack = 8, MonsterDefense = 2, SlotCount = 5, SortOrder = 1 },
            new Dungeon { Code = "goblin-camp", Name = "哥布林营地", MonsterName = "Goblin", MonsterElement = ElementType.Earth, MonsterMaxHp = 80, MonsterAttack = 12, MonsterDefense = 4, SlotCount = 5, SortOrder = 2 },
            new Dungeon { Code = "wolf-forest", Name = "狼群森林", MonsterName = "Wolf", MonsterElement = ElementType.Water, MonsterMaxHp = 65, MonsterAttack = 15, MonsterDefense = 3, SlotCount = 5, SortOrder = 3 }
        };

        var missing = defaults.Where(dungeon => !existingCodes.Contains(dungeon.Code)).ToList();
        if (missing.Count == 0) return;
        dbContext.Dungeons.AddRange(missing);
        await dbContext.SaveChangesAsync();
    }

    private static async Task AdoptLegacyEnsureCreatedDatabaseAsync(GameDbContext dbContext)
    {
        var databaseCreator = dbContext.GetService<IRelationalDatabaseCreator>();
        if (!await databaseCreator.ExistsAsync())
        {
            return;
        }

        var historyRepository = dbContext.GetService<IHistoryRepository>();
        if (await historyRepository.ExistsAsync())
        {
            return;
        }

        if (!await TableExistsAsync(dbContext, "Users"))
        {
            return;
        }

        await dbContext.Database.ExecuteSqlRawAsync(historyRepository.GetCreateScript());
        await InsertHistoryAsync(dbContext, historyRepository, InitialCreateMigrationId);

        if (await ColumnExistsAsync(dbContext, "Users", "ActiveCharacterId"))
        {
            await InsertHistoryAsync(dbContext, historyRepository, AddActiveCharacterMigrationId);
        }
    }

    private static Task InsertHistoryAsync(
        GameDbContext dbContext,
        IHistoryRepository historyRepository,
        string migrationId)
    {
        var historyRow = new HistoryRow(migrationId, "8.0.6");
        return dbContext.Database.ExecuteSqlRawAsync(historyRepository.GetInsertScript(historyRow));
    }

    private static async Task<bool> TableExistsAsync(GameDbContext dbContext, string tableName)
    {
        var count = await dbContext.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = {tableName}")
            .SingleAsync();

        return count > 0;
    }

    private static async Task<bool> ColumnExistsAsync(GameDbContext dbContext, string tableName, string columnName)
    {
        var count = await dbContext.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM pragma_table_info({tableName}) WHERE name = {columnName}")
            .SingleAsync();

        return count > 0;
    }
}
