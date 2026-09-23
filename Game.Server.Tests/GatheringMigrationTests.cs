using Game.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public sealed class GatheringMigrationTests
{
    [Fact]
    public async Task ExistingRoomSlotsRemainAfterAddingMilestonesAndGathering()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-gathering-migration-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using (var db = new GameDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20260923020000_AddCampWarehouse");
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO RoomSlots (Id, RoomId, SlotIndex, CharacterId, UserId, IsMainControl,
                        IsConfirmed, IsAutoEnabled, IsTemporaryAuto, PendingSkillSlotMask)
                    VALUES (1, 7, 1, 3, 2, 1, 0, 0, 0, 0);
                    """);
            }
            await using (var db = new GameDbContext(options))
            {
                await db.Database.MigrateAsync();
                var slot = await db.RoomSlots.SingleAsync();
                Assert.Equal((7, 3), (slot.RoomId, slot.CharacterId));
                Assert.False(slot.HasParticipatedInRun);
                Assert.Null(slot.LastParticipatedMonsterId);
                Assert.False(db.Database.HasPendingModelChanges());
            }
        }
        finally { File.Delete(path); }
    }
}
