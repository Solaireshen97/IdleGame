using Game.Server.Data;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public class RewardMigrationTests
{
    [Fact]
    public async Task ExistingRoomsAndUsersGainRewardDefaultsWithoutLosingData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-rewards-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using (var db = new GameDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20260921040000_AddWeaponSkills");
                await db.Database.ExecuteSqlRawAsync("INSERT INTO Users (Id, UserName, PasswordHash) VALUES (42, 'older', 'hash')");
                await db.Database.ExecuteSqlRawAsync("INSERT INTO Rooms (Id, DungeonId, MonsterId, OwnerUserId, SlotCount, Status, IsPreparationTimeoutEnabled, IsRepeatBattle, RoundNumber, Version) VALUES (42, 1, 1, 42, 5, 0, 1, 0, 0, 0)");
            }
            await using (var db = new GameDbContext(options))
            {
                await db.Database.MigrateAsync();
                var user = await db.Users.SingleAsync(item => item.Id == 42);
                var room = await db.Rooms.SingleAsync(item => item.Id == 42);
                Assert.Equal("older", user.UserName);
                Assert.Equal(0, user.Gold);
                Assert.Equal(0, user.Version);
                Assert.Equal(1, room.RunSequence);
                Assert.False(db.Database.HasPendingModelChanges());
                db.RewardRuns.Add(new RewardRun { RoomId = room.Id, Sequence = room.RunSequence, Status = "Defeat" });
                await db.SaveChangesAsync();
            }
        }
        finally { File.Delete(path); }
    }
}
