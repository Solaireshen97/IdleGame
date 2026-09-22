using Game.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public sealed class CharacterSlotMigrationTests
{
    [Fact]
    public async Task ExistingAccountsKeepAlreadyUsedSlotsWhenCharacterLimitIsAdded()
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
                Assert.Equal(3, users[0].CharacterSlotLimit);
                Assert.Equal(2, users[1].CharacterSlotLimit);
                Assert.False(db.Database.HasPendingModelChanges());
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
