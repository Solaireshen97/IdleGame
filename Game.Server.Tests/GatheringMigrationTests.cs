using Game.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public sealed class GatheringMigrationTests
{
    [Fact]
    public async Task RetiredElwynnRareOpportunitiesMoveWithoutLosingCounts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-rare-migration-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using (var db = new GameDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20260923080000_AddProfessionProgressionAndTalents");
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO CharacterGatheringOpportunities
                        (CharacterId, PointCode, AvailableCount, EarnedCount, SpentCount, Version)
                    VALUES (1, 'elwynn-earthroot', 1, 2, 1, 0),
                           (1, 'elwynn-briarthorn', 2, 3, 1, 0),
                           (2, 'elwynn-briarthorn', 3, 4, 1, 0);
                    """);
            }
            await using (var db = new GameDbContext(options))
            {
                await db.Database.MigrateAsync();
                var opportunities = await db.CharacterGatheringOpportunities.OrderBy(item => item.CharacterId).ToListAsync();
                Assert.Equal(2, opportunities.Count);
                Assert.All(opportunities, item => Assert.Equal("elwynn-earthroot", item.PointCode));
                Assert.Equal((3, 5, 2), (opportunities[0].AvailableCount, opportunities[0].EarnedCount,
                    opportunities[0].SpentCount));
                Assert.Equal((3, 4, 1), (opportunities[1].AvailableCount, opportunities[1].EarnedCount,
                    opportunities[1].SpentCount));
                Assert.False(db.Database.HasPendingModelChanges());
            }
        }
        finally { File.Delete(path); }
    }

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
                Assert.Empty(await db.ProductionTasks.ToListAsync());
                Assert.False(db.Database.HasPendingModelChanges());
            }
        }
        finally { File.Delete(path); }
    }
}
