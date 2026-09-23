using Game.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public sealed class WarehouseMigrationTests
{
    [Fact]
    public async Task AddingWarehouseKeepsExistingCharacterInventory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-warehouse-migration-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using (var db = new GameDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20260923010000_AddCharacterActivitiesAndRoomDeadline");
                await db.Database.ExecuteSqlRawAsync("INSERT INTO Users (Id, UserName, PasswordHash) VALUES (1, 'owner', 'hash')");
                await db.Database.ExecuteSqlRawAsync("INSERT INTO Characters (Id, UserId, Name, Hp, MaxHp, Attack) VALUES (1, 1, 'Fighter', 100, 100, 20)");
                await db.Database.ExecuteSqlRawAsync("INSERT INTO CharacterItemStacks (CharacterId, ItemCode, Quantity, Version) VALUES (1, 'minor-healing-potion', 7, 0)");
            }

            await using (var db = new GameDbContext(options))
            {
                await db.Database.MigrateAsync();
                Assert.Equal(7, (await db.CharacterItemStacks.SingleAsync()).Quantity);
                Assert.False(await db.UserWarehouseStacks.AnyAsync());
                Assert.False(await db.WarehouseTransferRecords.AnyAsync());
                Assert.False(db.Database.HasPendingModelChanges());
            }
        }
        finally { File.Delete(path); }
    }
}
