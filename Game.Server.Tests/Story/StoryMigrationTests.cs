using Game.Server.Data;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests.Story;

public class StoryMigrationTests
{
    private const string PreviousMigration = "20261002040000_RebalanceCombatConsumables";

    [Fact]
    public async Task ExistingAccountsKeepAssetsAndProgressThroughRollbackAndReapply()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idle-story-migration-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new GameDbContext(options);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
            db.Users.Add(new User { Id = 42, UserName = "older-story", PasswordHash = "hash" });
            db.Characters.Add(new Character { Id = 42, UserId = 42, Name = "Traveler", Gold = 237,
                Level = 7, Experience = 123, GatheringLevel = 3, GatheringExperience = 45,
                AlchemyLevel = 2, AlchemyExperience = 67, Version = 8 });
            await db.SaveChangesAsync();
            db.Users.Local.Single().ActiveCharacterId = 42;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var before = await db.Characters.AsNoTracking().SingleAsync();
            var beforeUser = await db.Users.AsNoTracking().SingleAsync();
            await migrator.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            var state = await db.UserStoryStates.AsNoTracking().SingleAsync();
            Assert.True(state.IsLegacy);
            Assert.Equal(42, state.TutorialCharacterId);
            Assert.Null(state.CurrentQuestCode);
            Assert.Equal("ch01", state.ChapterCode);
            Assert.Equal("[]", state.StoryFlagsJson);
            Assert.Empty(await db.StoryQuestProgress.ToListAsync());
            Assert.Empty(await db.StoryMapUnlocks.ToListAsync());
            await AssertPreserved(db, before, beforeUser);
            await migrator.MigrateAsync(PreviousMigration);
            await AssertPreserved(db, before, beforeUser);
            await migrator.MigrateAsync();
            Assert.True((await db.UserStoryStates.AsNoTracking().SingleAsync()).IsLegacy);
            await AssertPreserved(db, before, beforeUser);
            Assert.False(db.Database.HasPendingModelChanges());
        }
        finally { File.Delete(path); }
    }

    private static async Task AssertPreserved(GameDbContext db, Character before, User beforeUser)
    {
        var after = await db.Characters.AsNoTracking().SingleAsync();
        var afterUser = await db.Users.AsNoTracking().SingleAsync();
        Assert.Equal((before.Gold, before.Level, before.Experience, before.Version),
            (after.Gold, after.Level, after.Experience, after.Version));
        Assert.Equal((before.GatheringLevel, before.GatheringExperience, before.AlchemyLevel, before.AlchemyExperience),
            (after.GatheringLevel, after.GatheringExperience, after.AlchemyLevel, after.AlchemyExperience));
        Assert.Equal((beforeUser.ActiveCharacterId, beforeUser.CharacterSlotLimit, beforeUser.Version),
            (afterUser.ActiveCharacterId, afterUser.CharacterSlotLimit, afterUser.Version));
    }

    [Fact]
    public async Task EnsureCreatedAndMigrationsHaveMatchingStorySchema()
    {
        await using var createdConnection = new SqliteConnection("Data Source=:memory:");
        await using var migratedConnection = new SqliteConnection("Data Source=:memory:");
        await createdConnection.OpenAsync();
        await migratedConnection.OpenAsync();
        await using var created = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(createdConnection).Options);
        await using var migrated = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(migratedConnection).Options);
        await created.Database.EnsureCreatedAsync();
        await migrated.Database.MigrateAsync();
        Assert.False(created.Database.HasPendingModelChanges());
        Assert.False(migrated.Database.HasPendingModelChanges());
        foreach (var table in new[] { "UserStoryStates", "StoryQuestProgress", "StoryMapUnlocks", "StoryActionReceipts", "StoryEventReceipts" })
        {
            Assert.Equal(await ReadPragma(createdConnection, $"table_info('{table}')"), await ReadPragma(migratedConnection, $"table_info('{table}')"));
            Assert.Equal(await ReadPragma(createdConnection, $"foreign_key_list('{table}')"), await ReadPragma(migratedConnection, $"foreign_key_list('{table}')"));
        }
        Assert.Equal(await ReadPragma(createdConnection, "index_info('IX_StoryActionReceipts_UserId_QuestCode')"),
            await ReadPragma(migratedConnection, "index_info('IX_StoryActionReceipts_UserId_QuestCode')"));
    }

    private static async Task<string[]> ReadPragma(SqliteConnection connection, string pragma)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA " + pragma;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i).ToString())));
        return rows.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }
}
