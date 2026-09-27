using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class RareSeedServiceTests
{
    [Fact]
    public async Task SeedsOnlyRewardActualParticipantsOncePerEncounter()
    {
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var plants = new PlantingCatalog(Options.Create(new PlantingOptions
        {
            Plants = [new PlantOptions { Code = "rare-herb", Name = "Rare herb", SeedCode = "seed-rare-herb", MaterialCode = "rare-herb",
                IsRare = true, GrowthSeconds = 21600, HarvestQuantity = 8, UnlockKind = "MonsterKill", UnlockTargetCode = "elite-bear", DropChancePercent = 100 }]
        }));
        var service = new RareSeedService(db, plants);
        var room = new Room { Id = 91, RunSequence = 1 };
        RewardParticipant[] participants = [new(1, new Character { Id = 10, UserId = 1 }), new(1, new Character { Id = 11, UserId = 1 })];
        await service.RecordDropsAsync(room, "elite-bear", participants, [10]);
        await service.RecordDropsAsync(room, "elite-bear", participants, [10]);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await service.RecordDropsAsync(room, "elite-bear", participants, [10]);
        await db.SaveChangesAsync();
        var entry = await db.RewardEntries.SingleAsync();
        Assert.Equal((10, "seed-rare-herb", 1), (entry.CharacterId, entry.Code, entry.Quantity));
        Assert.Single(await db.RewardEvents.ToListAsync());
        room.RunSequence++;
        await service.RecordDropsAsync(room, "different-monster", participants, [10]);
        await service.RecordDropsAsync(room, "elite-bear", participants, [11]);
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.RewardEntries.CountAsync());
        Assert.Equal(11, (await db.RewardEntries.SingleAsync(e => e.Sequence == 2)).CharacterId);
    }

    [Fact]
    public async Task UnsuccessfulDropStillRecordsItsEventToPreventRerolling()
    {
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var plant = new PlantOptions { Code = "rare", Name = "Rare", SeedCode = "seed-rare", MaterialCode = "rare", IsRare = true,
            GrowthSeconds = 21600, HarvestQuantity = 8, UnlockTargetCode = "elite", DropChancePercent = 0 };
        var service = new RareSeedService(db, new PlantingCatalog(Options.Create(new PlantingOptions { Plants = [plant] })));
        var room = new Room { Id = 19, RunSequence = 1 };
        RewardParticipant[] participants = [new(1, new Character { Id = 1, UserId = 1 })];
        await service.RecordDropsAsync(room, "elite", participants, [1]);
        await db.SaveChangesAsync();
        plant.DropChancePercent = 100;
        await service.RecordDropsAsync(room, "elite", participants, [1]);
        await db.SaveChangesAsync();
        Assert.Empty(await db.RewardEntries.ToListAsync());
    }
}
