using System.Text.Json;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Story;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public sealed class StoryServiceIntegrationTests
{
    [Fact]
    public async Task ChapterProgressionAwardsOnceAndUnlocksMapsAtTurnIn()
    {
        await using var db = await CreateAsync();
        var service = Service(db);
        Assert.Null((await service.InitializeAsync("token")).Error);
        var initial = (await service.GetAsync("token")).Response!;
        Assert.Equal("ch01-01", initial.CurrentQuest!.Code);
        Assert.Equal("ReadyToTurnIn", initial.CurrentQuest.Status);
        Assert.All(initial.MapNodes, n => Assert.False(n.IsUnlocked));
        var first = await ClaimAsync(service, "ch01-01");
        Assert.True(first.MapNodes.Single(n => n.Code == "northshire-wolves").CanEnter);
        Assert.Equal("StoryObjectiveIncomplete", (await service.TurnInAsync("token", "ch01-02",
            new() { RequestId = Id(), ExpectedVersion = first.CurrentQuest!.Version })).Error);
        Assert.Equal("StoryQuestUnavailable", (await service.TurnInAsync("token", "ch01-11",
            new() { RequestId = Id() })).Error);
        await RecordAsync(db, "DungeonClear", "northshire-wolves");
        var view = (await service.GetAsync("token")).Response!;
        var claimId = Id();
        var request = new StoryTurnInRequest { RequestId = claimId, ExpectedVersion = view.CurrentQuest!.Version };
        Assert.Null((await service.TurnInAsync("token", "ch01-02", request)).Error);
        Assert.Equal(20, (await db.Characters.FindAsync(1))!.Gold);
        Assert.Equal(2, (await db.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Null((await service.TurnInAsync("token", "ch01-02", request)).Error);
        Assert.Null((await service.TurnInAsync("token", "ch01-02", new() { RequestId = Id() })).Error);
        Assert.Equal(20, (await db.Characters.FindAsync(1))!.Gold);
        Assert.Equal("RequestIdConflict", (await service.TurnInAsync("token", "ch01-03", request)).Error);
        db.CharacterWeapons.Add(new() { CharacterId = 1, WeaponCode = "loot", Name = "Loot", MaxHp = 1,
            Origin = WeaponOrigin.Drop, EquippedSlotIndex = 1,
            Skills = [new() { SlotIndex = 1, SkillCode = "skill", Level = 2, BaseLevel = 2 }] });
        await StoryProgressService.RefreshAsync(db, 1, DateTime.UtcNow);
        await db.SaveChangesAsync();
        await ClaimAsync(service, "ch01-03");
        await ClaimAsync(service, "ch01-04");
        await RecordAsync(db, "Plant", "peacebloom");
        await ClaimAsync(service, "ch01-05");
        db.AddRange(new CharacterItemStack { CharacterId = 1, ItemCode = "minor-healing-potion", Quantity = 1 },
            new CharacterConsumableSlot { CharacterId = 1, SlotIndex = 1, ItemCode = "minor-healing-potion" });
        await RecordAsync(db, "Produce", "minor-healing-potion");
        await ClaimAsync(service, "ch01-06");
        await RecordAsync(db, "DungeonClear", "stone-tusk-boars");
        await ClaimAsync(service, "ch01-07");
        await ClaimAsync(service, "ch01-08");
        await RecordAsync(db, "DungeonClear", "kobold-miners");
        await ClaimAsync(service, "ch01-09");
        await RecordAsync(db, "FormationSaved", "");
        await ClaimAsync(service, "ch01-10");
        await RecordAsync(db, "DungeonClear", "kobold-mine");
        await ClaimAsync(service, "ch01-11");
        var complete = await ClaimAsync(service, "ch01-12");
        Assert.True(complete.ChapterCompleted);
        Assert.Null(complete.CurrentQuest);
        Assert.Equal(12, await db.StoryActionReceipts.CountAsync(x => x.QuestCode != null));
        Assert.Equal(190, (await db.Characters.FindAsync(1))!.Gold);
        db.ChangeTracker.Clear();
        Assert.True((await service.GetAsync("token")).Response!.ChapterCompleted);
    }

    [Fact]
    public async Task VersionsOwnershipAndDeletedActorReplacementDoNotDuplicateSupplies()
    {
        await using var db = await CreateAsync();
        var service = Service(db);
        await service.InitializeAsync("token");
        await ClaimAsync(service, "ch01-01");
        var old = (await service.GetAsync("token")).Response!;
        await RecordAsync(db, "DungeonClear", "northshire-wolves");
        Assert.Equal("ConcurrencyConflict", (await service.TurnInAsync("token", "ch01-02",
            new() { RequestId = Id(), ExpectedVersion = old.CurrentQuest!.Version })).Error);
        await ClaimAsync(service, "ch01-02");
        var view = (await service.GetAsync("token")).Response!;
        Assert.Equal("NotOwner", (await service.ChangeCharacterAsync("token", new()
            { CharacterId = 3, ExpectedVersion = view.Version, RequestId = Id() })).Error);
        Assert.Equal("StoryQuestUnavailable", (await service.TurnInAsync("foreign", "ch01-03",
            new() { RequestId = Id() })).Error);
        var changed = await service.ChangeCharacterAsync("token", new()
            { CharacterId = 2, ExpectedVersion = view.Version, RequestId = Id() });
        Assert.Null(changed.Error);
        Assert.Equal("ConcurrencyConflict", (await service.ChangeCharacterAsync("token", new()
            { CharacterId = 1, ExpectedVersion = view.Version, RequestId = Id() })).Error);
        Assert.Empty(await db.CharacterItemStacks.Where(x => x.CharacterId == 2).ToListAsync());
        Assert.Equal(2, (await db.CharacterItemStacks.SingleAsync(x => x.CharacterId == 1)).Quantity);
        db.Characters.Remove((await db.Characters.FindAsync(2))!);
        await db.SaveChangesAsync();
        var deleted = (await service.GetAsync("token")).Response!;
        Assert.True(deleted.NeedsTutorialCharacter);
        Assert.Null((await service.ChangeCharacterAsync("token", new()
            { CharacterId = 1, ExpectedVersion = deleted.Version, RequestId = Id() })).Error);
        Assert.Equal(2, (await db.CharacterItemStacks.SingleAsync()).Quantity);
    }

    [Fact]
    public async Task LegacyReviewSkipsObjectivesWithoutAwardsOrMapRegression()
    {
        await using var db = await CreateAsync(true);
        var service = Service(db);
        await service.InitializeAsync("token");
        foreach (var definition in StoryQuestCatalog.Default.Quests)
        {
            var view = (await service.GetAsync("token")).Response!;
            Assert.Equal("ReadyToTurnIn", view.CurrentQuest!.Status);
            Assert.Equal(0, view.CurrentQuest.RewardGold);
            Assert.Empty(view.CurrentQuest.Rewards);
            Assert.All(view.MapNodes, node => Assert.True(node.IsUnlocked));
            await ClaimAsync(service, definition.Code);
        }
        Assert.Equal(0, (await db.Characters.FindAsync(1))!.Gold);
        Assert.Empty(await db.CharacterItemStacks.ToListAsync());
    }

    [Fact]
    public async Task OverflowRollsBackRewardMapsNextQuestAndReceipt()
    {
        await using var db = await CreateAsync();
        var service = Service(db);
        await service.InitializeAsync("token");
        await ClaimAsync(service, "ch01-01");
        await RecordAsync(db, "DungeonClear", "northshire-wolves");
        db.CharacterItemStacks.Add(new() { CharacterId = 1, ItemCode = "weapon-fragment-t1", Quantity = int.MaxValue });
        await db.SaveChangesAsync();
        var view = (await service.GetAsync("token")).Response!;
        Assert.Equal("InventoryFull", (await service.TurnInAsync("token", "ch01-02", new()
            { RequestId = Id(), ExpectedVersion = view.CurrentQuest!.Version })).Error);
        Assert.Equal(0, (await db.Characters.FindAsync(1))!.Gold);
        Assert.Equal("ReadyToTurnIn", (await db.StoryQuestProgress.SingleAsync(x => x.QuestCode == "ch01-02")).Status);
        Assert.False(await db.StoryQuestProgress.AnyAsync(x => x.QuestCode == "ch01-03"));
        Assert.Single(await db.StoryActionReceipts.ToListAsync());
    }

    [Fact]
    public async Task PotionPurchaseAndZeroInventoryCannotCompleteCraftingObjective()
    {
        await using var db = await CreateAsync();
        var definition = StoryQuestCatalog.Default.FindQuest("ch01-06")!;
        db.UserStoryStates.Single().CurrentQuestCode = definition.Code;
        db.UserStoryStates.Single().TutorialCharacterId = 1;
        db.StoryQuestProgress.Add(new() { UserId = 1, QuestCode = definition.Code, ActorCharacterId = 1,
            ActivatedAtUtc = DateTime.UtcNow.AddMinutes(-1), DefinitionJson = JsonSerializer.Serialize(definition) });
        var stack = new CharacterItemStack { CharacterId = 1, ItemCode = definition.TargetCode, Quantity = 5 };
        db.AddRange(stack, new CharacterConsumableSlot { CharacterId = 1, SlotIndex = 1, ItemCode = definition.TargetCode });
        await StoryProgressService.RefreshAsync(db, 1, DateTime.UtcNow);
        Assert.Equal("Active", db.StoryQuestProgress.Local.Single().Status);
        stack.Quantity = 0;
        await RecordAsync(db, "Produce", definition.TargetCode);
        Assert.Equal("Active", (await db.StoryQuestProgress.SingleAsync()).Status);
        stack.Quantity = 1;
        await StoryProgressService.RefreshAsync(db, 1, DateTime.UtcNow);
        await db.SaveChangesAsync();
        Assert.Equal("ReadyToTurnIn", (await db.StoryQuestProgress.SingleAsync()).Status);
    }

    [Fact]
    public async Task CurrentDefinitionSnapshotSurvivesNewCatalogRevision()
    {
        await using var db = await CreateAsync();
        var service = Service(db);
        await service.InitializeAsync("token");
        await ClaimAsync(service, "ch01-01");
        var options = JsonSerializer.Deserialize<StoryOptions>(JsonSerializer.Serialize(new StoryOptions
        { Chapters = StoryQuestCatalog.Default.Chapters.ToList(), Npcs = StoryQuestCatalog.Default.Npcs.ToList(),
          MapNodes = StoryQuestCatalog.Default.MapNodes.ToList(), Quests = StoryQuestCatalog.Default.Quests.ToList() }))!;
        options.Quests.Single(q => q.Code == "ch01-02").RewardGold = 999;
        options.Quests.Single(q => q.Code == "ch01-02").Revision++;
        var updated = Service(db, new StoryQuestCatalog(options));
        await RecordAsync(db, "DungeonClear", "northshire-wolves");
        Assert.Equal(20, (await updated.GetAsync("token")).Response!.CurrentQuest!.RewardGold);
        await ClaimAsync(updated, "ch01-02");
        Assert.Equal(20, (await db.Characters.FindAsync(1))!.Gold);
    }

    [Fact]
    public async Task QuestGiftCannotFeedPastProductionCyclesOrCompleteNextQuest()
    {
        await using var db = await CreateAsync();
        var definition = StoryQuestCatalog.Default.FindQuest("ch01-05")!;
        var state = await db.UserStoryStates.SingleAsync();
        state.CurrentQuestCode = definition.Code; state.TutorialCharacterId = 1;
        db.StoryQuestProgress.Add(new() { UserId = 1, QuestCode = definition.Code, ActorCharacterId = 1,
            Status = "ReadyToTurnIn", Progress = 1, ActivatedAtUtc = DateTime.UtcNow.AddMinutes(-2),
            DefinitionJson = JsonSerializer.Serialize(definition) });
        var now = DateTime.UtcNow;
        db.ProductionTasks.Add(new() { UserId = 1, CharacterId = 1, RecipeCode = "minor-healing-potion",
            OutputCode = "minor-healing-potion", OutputQuantity = 1, CycleSeconds = 10,
            IngredientsJson = JsonSerializer.Serialize(new[] { new ProductionIngredientOptions { Code = "peacebloom", Quantity = 2 } }),
            StartedAtUtc = now.AddMinutes(-1), NextCycleAtUtc = now.AddSeconds(-30), EndsAtUtc = now.AddHours(1) });
        await db.SaveChangesAsync();
        var users = new UserService(db, ProgressionTestFactory.Create(), SkillTestFactory.Create());
        var world = WorldCatalog.LoadDefault();
        var materials = new MaterialCatalog(Microsoft.Extensions.Options.Options.Create(new MaterialOptions()));
        var consumables = ConsumableTestFactory.Create();
        var productionCatalog = new ProductionCatalog(Microsoft.Extensions.Options.Options.Create(new ProductionOptions()), world, materials, consumables);
        var production = new ProductionService(db, users, productionCatalog, world, materials, consumables,
            Microsoft.Extensions.Options.Options.Create(new ActivityOptions { MaximumHours = 12 }));
        var service = new StoryService(db, users, StoryQuestCatalog.Default, production);
        var next = await ClaimAsync(service, "ch01-05");
        Assert.Equal("ch01-06", next.CurrentQuest!.Code);
        Assert.Equal("Active", next.CurrentQuest.Status);
        Assert.Equal(0, next.CurrentQuest.Progress);
        var task = await db.ProductionTasks.SingleAsync();
        Assert.Equal("MaterialShortage", task.Status);
        Assert.Equal(0, task.CompletedCycles);
        Assert.Equal(2, (await db.CharacterItemStacks.SingleAsync(x => x.ItemCode == "peacebloom")).Quantity);
        Assert.False(await db.CharacterItemStacks.AnyAsync(x => x.ItemCode == "minor-healing-potion"));
    }
    private static string Id() => Guid.NewGuid().ToString("N");
    private static StoryService Service(GameDbContext db, StoryQuestCatalog? catalog = null) => new(db,
        new UserService(db, ProgressionTestFactory.Create(), SkillTestFactory.Create()), catalog ?? StoryQuestCatalog.Default);
    private static async Task RecordAsync(GameDbContext db, string kind, string target)
    { await StoryProgressService.RecordAsync(db, 1, kind, target, Id(), 1, DateTime.UtcNow); await db.SaveChangesAsync(); }
    private static async Task<StoryOverviewResponse> ClaimAsync(StoryService service, string code)
    {
        var view = (await service.GetAsync("token")).Response!;
        Assert.Equal(code, view.CurrentQuest!.Code);
        var claimed = await service.TurnInAsync("token", code, new() { RequestId = Id(), ExpectedVersion = view.CurrentQuest.Version });
        Assert.Null(claimed.Error); return claimed.Response!;
    }
    private static async Task<GameDbContext> CreateAsync(bool legacy = false)
    {
        var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync();
        db.AddRange(new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 },
            new User { Id = 2, UserName = "other", PasswordHash = "x", ActiveCharacterId = 3 },
            new Character { Id = 1, UserId = 1, Name = "Hero", Level = 1 },
            new Character { Id = 2, UserId = 1, Name = "OtherHero", Level = 1 },
            new Character { Id = 3, UserId = 2, Name = "Foreign", Level = 1 },
            new UserLoginSession { UserId = 1, Token = "token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) },
            new UserLoginSession { UserId = 2, Token = "foreign", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) },
            new UserStoryState { UserId = 1, IsLegacy = legacy });
        foreach (var node in StoryQuestCatalog.Default.MapNodes)
            db.Dungeons.Add(new() { Code = node.DungeonCode, Name = node.Name, RegionCode = node.RegionCode,
                MonsterName = "Target", SlotCount = 5 });
        await db.SaveChangesAsync(); return db;
    }
}
