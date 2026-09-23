using Game.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public sealed class CharacterInventoryMigrationTests
{
    [Fact]
    public async Task ExistingWarehouseItemsMergeIntoTheActiveCharactersBag()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-character-inventory-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using (var db = new GameDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20260923050000_AddProductionAndAlchemy");
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO Users (Id, UserName, PasswordHash, ActiveCharacterId, Gold, Version)
                    VALUES (1, 'owner', 'x', 2, 90, 0);
                    INSERT INTO Characters (Id, UserId, Name, Hp, MaxHp, Attack, Level, Version)
                    VALUES (1, 1, 'First', 100, 100, 20, 1, 0),
                           (2, 1, 'Second', 100, 100, 20, 1, 0);
                    INSERT INTO CharacterItemStacks (CharacterId, ItemCode, Quantity, Version)
                    VALUES (1, 'peacebloom', 4, 0), (2, 'peacebloom', 3, 0);
                    INSERT INTO UserWarehouseStacks (UserId, ItemCode, Quantity, Version)
                    VALUES (1, 'peacebloom', 5, 0), (1, 'minor-healing-potion', 2, 0);
                    INSERT INTO UserDungeonClears (UserId, DungeonId, ClearedAtUtc)
                    VALUES (1, 1, '2026-09-23 00:00:00');
                    """);
            }

            await using (var db = new GameDbContext(options))
            {
                await db.Database.MigrateAsync();
                Assert.Equal(4, (await db.CharacterItemStacks.SingleAsync(item =>
                    item.CharacterId == 1 && item.ItemCode == "peacebloom")).Quantity);
                Assert.Equal(8, (await db.CharacterItemStacks.SingleAsync(item =>
                    item.CharacterId == 2 && item.ItemCode == "peacebloom")).Quantity);
                Assert.Equal(2, (await db.CharacterItemStacks.SingleAsync(item =>
                    item.CharacterId == 2 && item.ItemCode == "minor-healing-potion")).Quantity);
                Assert.Equal(0, (await db.Characters.SingleAsync(item => item.Id == 1)).Gold);
                Assert.Equal(90, (await db.Characters.SingleAsync(item => item.Id == 2)).Gold);
                var clear = Assert.Single(await db.CharacterBattleMilestones.ToListAsync());
                Assert.Equal(2, clear.CharacterId);
                Assert.Equal("DungeonClear", clear.Kind);
                Assert.Equal((await db.Dungeons.SingleAsync(item => item.Id == 1)).Code, clear.TargetCode);
                var oldTables = await db.Database.SqlQueryRaw<int>("""
                    SELECT COUNT(*) AS Value FROM sqlite_master
                    WHERE type = 'table' AND name IN ('UserWarehouseStacks', 'WarehouseTransferRecords')
                    """).SingleAsync();
                Assert.Equal(0, oldTables);
                Assert.False(db.Database.HasPendingModelChanges());
            }
        }
        finally { File.Delete(path); }
    }
}
