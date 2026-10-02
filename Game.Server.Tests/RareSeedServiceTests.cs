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
    public async Task FirstSeedGuaranteeIsPerCharacterAndCannotBeClaimedAgainAfterRestart()
    {
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        db.AddRange(new User { Id = 1, UserName = "owner", ActiveCharacterId = 1 },
            new Character { Id = 1, UserId = 1, Name = "First" }, new Character { Id = 2, UserId = 1, Name = "Second" });
        await db.SaveChangesAsync();
        var plant = new PlantOptions { Code = "rare", Name = "Rare", SeedCode = "seed-rare", MaterialCode = "rare", IsRare = true,
            GrowthSeconds = 21600, HarvestQuantity = 2, UnlockKind = "DungeonClear", UnlockTargetCode = "deep",
            DropChancePercent = 0, FirstClearGuaranteed = true };
        var catalog = new PlantingCatalog(Options.Create(new PlantingOptions { Plants = [plant] }));
        var service = new RareSeedService(db, catalog);
        var room = new Room { Id = 91, RunSequence = 1, DepthLevel = 1 };
        RewardParticipant[] participants = [new(1, new Character { Id = 1, UserId = 1 }), new(1, new Character { Id = 2, UserId = 1 })];
        await service.RecordDropsAsync(room, "deep", participants, [1]);
        await service.RecordDropsAsync(room, "deep", participants, [1]);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        service = new RareSeedService(db, catalog);
        await service.RecordDropsAsync(room, "deep", participants, [1]);
        room.RunSequence++;
        await service.RecordDropsAsync(room, "deep", participants, [1]);
        await db.SaveChangesAsync();
        Assert.Single(await db.RewardEntries.ToListAsync());
        Assert.Single(await db.CharacterBattleMilestones.Where(m => m.Kind == RareSeedService.FirstSeedClearKind).ToListAsync());
        room.RunSequence++;
        await service.RecordDropsAsync(room, "deep", participants, [2]);
        await db.SaveChangesAsync();
        Assert.Equal(new[] { 1, 2 }, await db.RewardEntries.OrderBy(e => e.CharacterId).Select(e => e.CharacterId).ToArrayAsync());
        Assert.All(await db.RewardEntries.ToListAsync(), e => Assert.Equal((1, "SeedFirstClear"), (e.Quantity, e.RewardSource)));
    }

    [Fact]
    public async Task OrdinaryDropUsesAttemptDepthAndCapsProbability()
    {
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var plant = new PlantOptions { Code = "rare", Name = "Rare", SeedCode = "seed-rare", MaterialCode = "rare", IsRare = true,
            GrowthSeconds = 21600, HarvestQuantity = 2, UnlockTargetCode = "deep", DropChancePercent = 0, DropChancePerDepthPercent = 100 };
        var catalog = new PlantingCatalog(Options.Create(new PlantingOptions { Plants = [plant] }));
        var service = new RareSeedService(db, catalog);
        var room = new Room { Id = 19, RunSequence = 1, DepthLevel = 1 };
        RewardParticipant[] participants = [new(1, new Character { Id = 1, UserId = 1 }), new(2, new Character { Id = 2, UserId = 1 })];
        await service.RecordDropsAsync(room, "deep", participants, [1, 2]);
        await db.SaveChangesAsync();
        Assert.Empty(await db.RewardEntries.ToListAsync());
        room.RunSequence++;
        room.DepthLevel = 2;
        await service.RecordDropsAsync(room, "deep", participants, [1, 2]);
        await db.SaveChangesAsync();
        Assert.Equal(1, (await db.RewardEntries.SingleAsync()).CharacterId);
        Assert.Equal(100, Assert.Single(catalog.SeedDropsFor("deep", 10)).DropChancePercent);
    }

    [Fact]
    public async Task FrozenEntryStopsOldSeedDropsAndFrozenDeepWithEmptyPoolStartsNewSeedDrops()
    {
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        db.AddRange(new User { Id = 1, UserName = "owner", ActiveCharacterId = 1 }, new Character { Id = 1, UserId = 1, Name = "First" });
        foreach (var (id, code) in new[] { (1, "entry"), (2, "deep") })
            db.AddRange(new Dungeon { Id = id, Code = code, Name = code, DungeonKind = "Dungeon" },
                new Room { Id = id, DungeonId = id, MonsterId = id, RunSequence = 1, DepthLevel = 4 },
                new Monster { Id = id, RoomId = id, WaveNumber = 1, Position = 1, Name = "Enemy", Hp = 10, MaxHp = 10, BaseMaxHp = 10 });
        await db.SaveChangesAsync();
        PlantingCatalog Plants(string target, bool first, int chance) => new(Options.Create(new PlantingOptions
        {
            Plants = [new() { Code = "rare", Name = "Rare", SeedCode = "seed-rare", MaterialCode = "rare", IsRare = true,
                GrowthSeconds = 21600, HarvestQuantity = 2, UnlockKind = "DungeonClear", UnlockTargetCode = target,
                DropChancePercent = chance, FirstClearGuaranteed = first }]
        }));
        DungeonRunRulesService Rules(PlantingCatalog plants) => new(db,
            new MonsterCombatCatalog(Options.Create(new MonsterCombatOptions())), RewardTestFactory.CreateCatalog(),
            PartyScalingCatalog.Default, new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions())), plants: plants);
        var oldRules = Rules(Plants("entry", false, 100));
        Assert.Single((await oldRules.EnsureAsync((await db.Rooms.FindAsync(1))!)).RareSeeds);
        Assert.Empty((await oldRules.EnsureAsync((await db.Rooms.FindAsync(2))!)).RareSeeds);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var live = Plants("deep", true, 0);
        var service = new RareSeedService(db, live, Rules(live));
        RewardParticipant[] participants = [new(1, (await db.Characters.FindAsync(1))!)];
        await service.RecordDropsAsync((await db.Rooms.FindAsync(1))!, "entry", participants, [1]);
        await service.RecordDropsAsync((await db.Rooms.FindAsync(2))!, "deep", participants, [1]);
        await db.SaveChangesAsync();
        Assert.Equal((2, "SeedFirstClear"), ((await db.RewardEntries.SingleAsync()).RoomId, (await db.RewardEntries.SingleAsync()).RewardSource));
    }

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
