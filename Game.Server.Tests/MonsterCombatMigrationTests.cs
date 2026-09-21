using Game.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public class MonsterCombatMigrationTests
{
    [Fact]
    public async Task ExistingSlimeEncounterMonstersReceiveCombatProfiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-monster-migration-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using (var db = new GameDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20260921060000_AddDungeonEncounters");
                var dungeonId = await db.Dungeons.Where(dungeon => dungeon.Code == "slime-field")
                    .Select(dungeon => dungeon.Id).SingleAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO Rooms (Id, DungeonId, MonsterId, OwnerUserId, SlotCount, Status,
                        IsPreparationTimeoutEnabled, IsRepeatBattle, RoundNumber, RunSequence,
                        CurrentWaveNumber, TotalWaveCount, Version)
                    VALUES (77, {dungeonId}, 701, 1, 5, 0, 1, 0, 0, 1, 1, 3, 0);
                    INSERT INTO Monsters (Id, RoomId, WaveNumber, Position, Name, Element, Hp, MaxHp, Attack, Defense)
                    VALUES
                        (701, 77, 1, 1, 'Slime', 'Wind', 35, 35, 6, 1),
                        (702, 77, 2, 1, 'Slime', 'Wind', 40, 40, 7, 2),
                        (703, 77, 2, 2, 'Slime', 'Wind', 40, 40, 7, 2),
                        (704, 77, 3, 1, 'King Slime', 'Wind', 70, 70, 10, 3);
                    """);
            }

            await using (var db = new GameDbContext(options))
            {
                await db.Database.MigrateAsync();
                var profiles = await db.Monsters.OrderBy(monster => monster.Id)
                    .Select(monster => monster.CombatProfileCode).ToArrayAsync();
                Assert.Equal(new[] { "", "slime-acid", "slime-hardened", "king-slime" }, profiles);
                Assert.False(db.Database.HasPendingModelChanges());
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
