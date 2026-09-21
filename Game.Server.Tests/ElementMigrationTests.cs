using Game.Server.Data;
using Game.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public class ElementMigrationTests
{
    [Fact]
    public async Task ExistingRoomsReceiveTheirDungeonsMonsterElement()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-element-migration-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options);
            await db.GetService<IMigrator>().MigrateAsync("20260921020000_AddWeaponLoadout");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO Monsters (Id, Name, Hp, MaxHp, Attack, Defense)
                VALUES (1, 'Slime', 50, 50, 8, 2), (2, 'Goblin', 80, 80, 12, 4), (3, 'Wolf', 65, 65, 15, 3);
                """);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO Rooms (Id, MonsterId, Status, DungeonId, OwnerUserId, SlotCount)
                VALUES (1, 1, 0, 1, 1, 5), (2, 2, 0, 2, 1, 5), (3, 3, 0, 3, 1, 5);
                """);

            await db.Database.MigrateAsync();

            Assert.Equal(new[] { ElementType.Wind, ElementType.Earth, ElementType.Water },
                await db.Dungeons.OrderBy(dungeon => dungeon.Id).Select(dungeon => dungeon.MonsterElement).ToArrayAsync());
            Assert.Equal(new[] { ElementType.Wind, ElementType.Earth, ElementType.Water },
                await db.Monsters.OrderBy(monster => monster.Id).Select(monster => monster.Element).ToArrayAsync());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
