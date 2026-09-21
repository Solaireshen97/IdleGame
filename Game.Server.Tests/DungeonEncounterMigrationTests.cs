using Game.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public class DungeonEncounterMigrationTests
{
    [Fact]
    public async Task ExistingActiveMonsterIsBackfilledAsFirstWave()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-encounters-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using (var db = new GameDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20260921050000_AddRewardRuns");
                await db.Database.ExecuteSqlRawAsync("INSERT INTO Monsters (Id, Name, Element, Hp, MaxHp, Attack, Defense) VALUES (7, 'Slime', 'Wind', 40, 50, 8, 2)");
                await db.Database.ExecuteSqlRawAsync("INSERT INTO Rooms (Id, DungeonId, MonsterId, OwnerUserId, SlotCount, Status, IsPreparationTimeoutEnabled, IsRepeatBattle, RoundNumber, RunSequence, Version) VALUES (9, 1, 7, 1, 5, 0, 1, 0, 0, 1, 0)");
            }

            await using (var db = new GameDbContext(options))
            {
                await db.Database.MigrateAsync();
                var room = await db.Rooms.SingleAsync();
                var monster = await db.Monsters.SingleAsync();
                Assert.Equal(1, room.CurrentWaveNumber);
                Assert.Equal(1, room.TotalWaveCount);
                Assert.Equal(room.Id, monster.RoomId);
                Assert.Equal(1, monster.WaveNumber);
                Assert.Equal(1, monster.Position);
                Assert.False(db.Database.HasPendingModelChanges());
            }
        }
        finally { File.Delete(path); }
    }
}
