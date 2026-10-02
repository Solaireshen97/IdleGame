using System.Text.Json;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Characters;
using Game.Shared.Dtos.Planting;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class StoryBusinessHookTests
{
    [Fact]
    public async Task PlantCountsOnlySuccessfulPlotsAndRequestReplayDoesNotCountAgain()
    {
        await using var db = await CreateAsync("Plant", "peacebloom", 3);
        db.CharacterItemStacks.Add(new() { CharacterId = 1, ItemCode = "seed-peacebloom", Quantity = 4 });
        await db.SaveChangesAsync();
        var catalog = new PlantingCatalog(Options.Create(new PlantingOptions { Plants = [new()
        {
            Code = "peacebloom", Name = "Herb", SeedCode = "seed-peacebloom", MaterialCode = "peacebloom",
            IsRare = true, GrowthSeconds = 60, HarvestQuantity = 3, UnlockTargetCode = "northshire-wolves"
        }] }));
        var service = new PlantingService(db, Users(db), catalog);
        await service.GetAsync("token");
        var request = new PlantGardenRequest(1, Guid.NewGuid().ToString(), "peacebloom", [new(0, 0), new(1, 0)]);
        Assert.Null((await service.PlantAsync("token", request)).Error);
        Assert.Null((await service.PlantAsync("token", request)).Error);
        Assert.Equal("PlotOccupied", (await service.PlantAsync("token", request with
        { RequestId = Guid.NewGuid().ToString(), Plots = [new(0, 1)] })).Error);
        Assert.Equal(2, (await db.StoryQuestProgress.SingleAsync()).Progress);
        Assert.Equal(2, await db.StoryEventReceipts.CountAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(2, (await db.StoryQuestProgress.SingleAsync()).Progress);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OfflineProductionAndPotionConfigurationCompleteInEitherOrder(bool equipFirst)
    {
        await using var db = await CreateAsync("ProduceAndEquipPotion", "minor-healing-potion", 1);
        var users = Users(db);
        var consumables = ConsumableTestFactory.Create();
        var slots = new ConsumableService(db, users, consumables);
        var request = new SetConsumableSlotRequest { ItemCode = "minor-healing-potion" };
        if (equipFirst) Assert.Null((await slots.SetSlotAsync("token", 1, 1, request)).Error);
        Assert.Equal("Active", (await db.StoryQuestProgress.SingleAsync()).Status);
        var now = DateTime.UtcNow;
        var task = new ProductionTask
        {
            UserId = 1, CharacterId = 1, RecipeCode = "minor-healing-potion", OutputCode = "minor-healing-potion",
            OutputQuantity = 1, CycleSeconds = 10, IngredientsJson = "[]", TargetCycles = 1,
            StartedAtUtc = now.AddSeconds(-10), NextCycleAtUtc = now, EndsAtUtc = now.AddHours(1)
        };
        db.ProductionTasks.Add(task);
        await db.SaveChangesAsync();
        var world = WorldCatalog.LoadDefault();
        var materials = new MaterialCatalog(Options.Create(new MaterialOptions()));
        var catalog = new ProductionCatalog(Options.Create(new ProductionOptions()), world, materials, consumables);
        var production = new ProductionService(db, users, catalog, world, materials, consumables,
            Options.Create(new ActivityOptions { MaximumHours = 12 }));
        await production.SettleCharacterTrackedAsync(1, now);
        await db.SaveChangesAsync();
        if (!equipFirst)
        {
            Assert.Equal("Active", (await db.StoryQuestProgress.SingleAsync()).Status);
            Assert.Null((await slots.SetSlotAsync("token", 1, 1, request)).Error);
        }
        Assert.Equal("ReadyToTurnIn", (await db.StoryQuestProgress.SingleAsync()).Status);
        await production.SettleCharacterTrackedAsync(1, now.AddHours(1));
        await db.SaveChangesAsync();
        Assert.Single(await db.StoryEventReceipts.ToListAsync());
        db.ChangeTracker.Clear();
        Assert.Equal("ReadyToTurnIn", (await db.StoryQuestProgress.SingleAsync()).Status);
    }

    [Fact]
    public async Task ActualBattleClearIgnoresLateJoinersAndDuplicateSettlement()
    {
        await using var db = await CreateAsync("DungeonClear", "slime-field", 2);
        var character = await db.Characters.SingleAsync();
        db.AddRange(new Dungeon { Id = 1, Code = "slime-field", Name = "Slime", MonsterName = "Slime", SlotCount = 5 },
            new Monster { Id = 1, Name = "Slime", Hp = 0, MaxHp = 10 },
            new Room { Id = 1, DungeonId = 1, MonsterId = 1, OwnerUserId = 1, SlotCount = 5, RunSequence = 1 });
        await db.SaveChangesAsync();
        var room = await db.Rooms.SingleAsync();
        var monster = await db.Monsters.SingleAsync();
        var runs = new DungeonRunService(db, RewardTestFactory.CreateService(db, ProgressionTestFactory.Create()));
        var participants = new[] { new RewardParticipant(1, character) };
        Assert.Null((await runs.AdvanceAfterDefeatAsync(room, monster, participants, DateTime.UtcNow, [], [1], [])).Error);
        await db.SaveChangesAsync();
        Assert.Equal(0, (await db.StoryQuestProgress.SingleAsync()).Progress);
        room.RunSequence++;
        Assert.Null((await runs.AdvanceAfterDefeatAsync(room, monster, participants, DateTime.UtcNow, [], [1], [1])).Error);
        await db.SaveChangesAsync();
        Assert.Null((await runs.AdvanceAfterDefeatAsync(room, monster, participants, DateTime.UtcNow, [], [1], [1])).Error);
        await db.SaveChangesAsync();
        Assert.Equal(1, (await db.StoryQuestProgress.SingleAsync()).Progress);
        Assert.Single(await db.StoryEventReceipts.ToListAsync());
    }

    [Fact]
    public async Task PotionConfigurationWithoutStoryStateKeepsExistingBehavior()
    {
        await using var db = await CreateAsync("ProduceAndEquipPotion", "minor-healing-potion", 1);
        db.UserStoryStates.RemoveRange(db.UserStoryStates);
        db.StoryQuestProgress.RemoveRange(db.StoryQuestProgress);
        await db.SaveChangesAsync();
        var service = new ConsumableService(db, Users(db), ConsumableTestFactory.Create());
        Assert.Null((await service.SetSlotAsync("token", 1, 1,
            new SetConsumableSlotRequest { ItemCode = "minor-healing-potion" })).Error);
        Assert.Empty(await db.StoryEventReceipts.ToListAsync());
        Assert.Empty(await db.StoryQuestProgress.ToListAsync());
        Assert.Equal("minor-healing-potion", (await db.CharacterConsumableSlots.SingleAsync()).ItemCode);
    }
    [Fact]
    public async Task ProductionAndProgressRollBackTogetherWithOuterTransaction()
    {
        await using var db = await CreateAsync("ProduceAndEquipPotion", "minor-healing-potion", 1);
        var now = DateTime.UtcNow;
        db.AddRange(new CharacterConsumableSlot { CharacterId = 1, SlotIndex = 1, ItemCode = "minor-healing-potion" },
            new ProductionTask { UserId = 1, CharacterId = 1, RecipeCode = "minor-healing-potion",
                OutputCode = "minor-healing-potion", OutputQuantity = 1, CycleSeconds = 10, IngredientsJson = "[]",
                StartedAtUtc = now.AddSeconds(-10), NextCycleAtUtc = now, EndsAtUtc = now.AddHours(1), TargetCycles = 1 });
        await db.SaveChangesAsync();
        var world = WorldCatalog.LoadDefault();
        var materials = new MaterialCatalog(Options.Create(new MaterialOptions()));
        var consumables = ConsumableTestFactory.Create();
        var catalog = new ProductionCatalog(Options.Create(new ProductionOptions()), world, materials, consumables);
        var service = new ProductionService(db, Users(db), catalog, world, materials, consumables,
            Options.Create(new ActivityOptions { MaximumHours = 12 }));
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await service.SettleCharacterTrackedAsync(1, now);
            await db.SaveChangesAsync();
            Assert.Equal("ReadyToTurnIn", (await db.StoryQuestProgress.SingleAsync()).Status);
            await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Equal("Active", (await db.StoryQuestProgress.SingleAsync()).Status);
        Assert.Equal(0, (await db.ProductionTasks.SingleAsync()).CompletedCycles);
        Assert.Empty(await db.StoryEventReceipts.ToListAsync());
        Assert.Empty(await db.CharacterItemStacks.ToListAsync());
    }
    private static UserService Users(GameDbContext db) => new(db, ProgressionTestFactory.Create(), SkillTestFactory.Create());

    private static async Task<GameDbContext> CreateAsync(string objective, string target, int required)
    {
        var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        db.AddRange(new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 },
            new Character { Id = 1, UserId = 1, Name = "Hero", Level = 1 },
            new UserLoginSession { UserId = 1, Token = "token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) },
            new UserStoryState { UserId = 1, TutorialCharacterId = 1, CurrentQuestCode = "hook-test" },
            new StoryQuestProgress
            {
                UserId = 1, QuestCode = "hook-test", ActorCharacterId = 1, ActivatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                DefinitionJson = JsonSerializer.Serialize(new StoryQuestDefinition
                { Code = "hook-test", ObjectiveType = objective, TargetCode = target, RequiredCount = required, ActorPolicy = "TutorialCharacter" })
            });
        await db.SaveChangesAsync();
        return db;
    }
}
