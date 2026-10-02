using Game.Server.Data;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public sealed class CharacterSlotMigrationTests
{
    [Fact]
    public async Task OlderAccountsKeepCharactersAndReceiveFiveSlotsWhenUpgraded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-character-slots-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using (var db = new GameDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20260922050000_AddFormalTalentTreesAndPromotions");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO Users (Id, UserName, PasswordHash) VALUES (1, 'veteran', 'hash'), (2, 'newer', 'hash')");
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO Characters (Id, UserId, Name, Hp, MaxHp, Attack)
                    VALUES (1, 1, 'one', 10, 10, 1),
                           (2, 1, 'two', 10, 10, 1),
                           (3, 1, 'three', 10, 10, 1),
                           (4, 2, 'only', 10, 10, 1);
                    """);
            }

            await using (var db = new GameDbContext(options))
            {
                await db.Database.MigrateAsync();
                var users = await db.Users.OrderBy(user => user.Id).ToListAsync();
                Assert.All(users, user => Assert.Equal(5, user.CharacterSlotLimit));
                Assert.Equal(4, await db.Characters.CountAsync());
                Assert.False(db.Database.HasPendingModelChanges());
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
    [Fact]
    public async Task FreeSlotGrantPreservesExistingAccountsAndAssetsAndIsNotRevokedByRollback()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        const string previous = "20261002050000_AddStoryCampaign";
        await migrator.MigrateAsync(previous);
        for (var slots = 2; slots <= 5; slots++)
        {
            db.Users.Add(new User { Id = slots, UserName = $"slots-{slots}", PasswordHash = "hash", CharacterSlotLimit = slots, Version = 7 });
            db.Characters.Add(new Character { Id = slots, UserId = slots, Name = $"hero-{slots}", Gold = 1234, Level = 8, Experience = 123, Version = 4 });
        }
        await db.SaveChangesAsync();
        foreach (var user in db.Users.Local) user.ActiveCharacterId = user.Id;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await migrator.MigrateAsync();

        var users = await db.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync();
        Assert.All(users, user =>
        {
            Assert.Equal(5, user.CharacterSlotLimit);
            Assert.Equal(user.Id, user.ActiveCharacterId);
            Assert.Equal(user.Id == 5 ? 7 : 8, user.Version);
        });
        Assert.All(await db.Characters.AsNoTracking().ToListAsync(), character =>
        {
            Assert.Equal(1234, character.Gold);
            Assert.Equal(8, character.Level);
            Assert.Equal(123, character.Experience);
            Assert.Equal(4, character.Version);
        });
        await migrator.MigrateAsync(previous);
        Assert.All(await db.Users.AsNoTracking().ToListAsync(), user => Assert.Equal(5, user.CharacterSlotLimit));
        await migrator.MigrateAsync();
        Assert.Equal(users.Select(u => u.Version), await db.Users.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Version).ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
