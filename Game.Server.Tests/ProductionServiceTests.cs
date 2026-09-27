using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Production;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class ProductionServiceTests
{
    private const string RecipeCode = "minor-healing-potion";
    private const string HerbCode = "peacebloom";

    [Fact]
    public async Task ProductionOutputSurvivesStaleBattleConsumptionAndFailedQuotaRollsBack()
    {
        await using var test = await ProductionTestContext.CreateAsync(4);
        test.First.Hp = 20;
        test.Db.AddRange(
            new Dungeon { Id = 1, Code = "training", Name = "Training", MonsterName = "Target",
                MonsterMaxHp = 10000, MonsterAttack = 1, SlotCount = 5 },
            new Monster { Id = 1, Name = "Target", Hp = 10000, MaxHp = 10000, Attack = 1 },
            new Room { Id = 1, DungeonId = 1, MonsterId = 1, OwnerUserId = 1, SlotCount = 5,
                Status = RoomStatus.NotStarted },
            new RoomSlot { RoomId = 1, SlotIndex = 1, UserId = 1, CharacterId = test.First.Id },
            new CharacterItemStack { CharacterId = test.First.Id, ItemCode = RecipeCode, Quantity = 5 },
            new CharacterConsumableSlot { CharacterId = test.First.Id, SlotIndex = 1, ItemCode = RecipeCode,
                AutoUseEnabled = true, AutoHpThresholdPercent = 100 });
        await test.Db.SaveChangesAsync();
        var started = await test.Service.StartAsync(test.Token, new StartProductionRequest
        {
            RequestId = Guid.NewGuid().ToString("N"), CharacterId = test.First.Id, RecipeCode = RecipeCode,
            TargetCycles = 1
        });
        Assert.Null(started.Error);
        await using var staleDb = test.NewDbContext();
        await staleDb.CharacterItemStacks.Where(stack => stack.CharacterId == test.First.Id).LoadAsync();
        await staleDb.Characters.FindAsync(test.First.Id);
        await staleDb.Rooms.SingleAsync();
        Assert.Null(await test.Service.AdvanceDueAsync(started.Response!.ActiveTask!.Id,
            started.Response.ActiveTask.NextCycleAtUtc));
        BattleService Battle(GameDbContext db)
        {
            var progression = ProgressionTestFactory.Create();
            return new BattleService(db, new UserService(db, progression, SkillTestFactory.Create()),
                ConsumableTestFactory.Create(), SkillTestFactory.Create(), RewardTestFactory.CreateService(db, progression));
        }
        Assert.Equal("ConcurrencyConflict", (await Battle(staleDb).StartPreparationAsync(1, test.Token)).Error);
        await using var verification = test.NewDbContext();
        Assert.Equal(6, (await verification.CharacterItemStacks.SingleAsync(stack => stack.ItemCode == RecipeCode)).Quantity);
        Assert.Empty(await verification.BattleHealingPotionStates.ToListAsync());
        Assert.Equal(20, (await verification.Characters.FindAsync(test.First.Id))!.Hp);
        Assert.Equal(0, (await verification.Rooms.SingleAsync()).RoundNumber);
        Assert.Null((await Battle(verification).StartPreparationAsync(1, test.Token)).Error);
        Assert.Equal(5, (await verification.CharacterItemStacks.SingleAsync(stack => stack.ItemCode == RecipeCode)).Quantity);
        Assert.Equal(1, (await verification.BattleHealingPotionStates.SingleAsync()).UsesUsed);
    }

    [Fact]
    public async Task FixedBatchIgnoresProfessionAndDuplicateRequestCannotProduceTwice()
    {
        await using var test = await ProductionTestContext.CreateAsync(20);
        var request = new StartProductionRequest { RequestId = Guid.NewGuid().ToString("N"), CharacterId = test.First.Id, RecipeCode = RecipeCode, TargetCycles = 3 };
        var started = await test.Service.StartAsync(test.Token, request);
        Assert.Null(started.Error);
        Assert.Null((await test.Service.StartAsync(test.Token, request)).Error);
        Assert.Single(await test.Db.ProductionTasks.ToListAsync());
        var task = started.Response!.ActiveTask!;
        Assert.Null(await test.Service.AdvanceDueAsync(task.Id, task.StartedAtUtc.AddHours(2)));
        Assert.Null(await test.Service.AdvanceDueAsync(task.Id, task.StartedAtUtc.AddHours(3)));
        var saved = await test.Db.ProductionTasks.SingleAsync();
        Assert.Equal(("Completed", 3, 3), (saved.Status, saved.CompletedCycles, saved.TotalQuantity));
        Assert.Equal(1, test.First.AlchemyLevel);
        Assert.Equal(0, test.First.AlchemyExperience);
        request.TargetCycles = 4;
        Assert.Equal("RequestIdConflict", (await test.Service.StartAsync(test.Token, request)).Error);
    }

    [Fact]
    public async Task SettlementBeforeNewMaterialsStopsPastCyclesAtShortage()
    {
        await using var test = await ProductionTestContext.CreateAsync(2);
        var started = await test.Service.StartAsync(test.Token, new StartProductionRequest { RequestId = Guid.NewGuid().ToString("N"), CharacterId = test.First.Id, RecipeCode = RecipeCode });
        var task = started.Response!.ActiveTask!;
        await test.Service.SettleCharacterTrackedAsync(test.First.Id, task.StartedAtUtc.AddSeconds(30));
        var herb = await test.Db.CharacterItemStacks.SingleAsync(item => item.CharacterId == test.First.Id && item.ItemCode == HerbCode);
        herb.Quantity += 20;
        await test.Db.SaveChangesAsync();
        Assert.Null(await test.Service.AdvanceDueAsync(task.Id, task.StartedAtUtc.AddSeconds(60)));
        Assert.Equal("MaterialShortage", (await test.Db.ProductionTasks.FindAsync(task.Id))!.Status);
        Assert.Equal(20, herb.Quantity);
    }

    [Fact]
    public async Task BattleActivityDoesNotBlockAlchemyOrGetReplaced()
    {
        await using var test = await ProductionTestContext.CreateAsync(2);
        test.Db.CharacterActivities.Add(new CharacterActivity { CharacterId = test.First.Id, Kind = "Battle", SourceId = 99, StartedAtUtc = DateTime.UtcNow });
        await test.Db.SaveChangesAsync();
        var started = await test.Service.StartAsync(test.Token, new StartProductionRequest { RequestId = Guid.NewGuid().ToString("N"), CharacterId = test.First.Id, RecipeCode = RecipeCode });
        Assert.Null(started.Error);
        Assert.Null((await test.Service.StopAsync(test.Token, started.Response!.ActiveTask!.Id)).Error);
        Assert.Equal("Battle", (await test.Db.CharacterActivities.SingleAsync()).Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4321)]
    public async Task InvalidFixedBatchCountsAreRejected(int count)
    {
        await using var test = await ProductionTestContext.CreateAsync(2);
        Assert.Equal("InvalidTargetCycles", (await test.Service.StartAsync(test.Token,
            new StartProductionRequest { RequestId = Guid.NewGuid().ToString("N"), CharacterId = test.First.Id, RecipeCode = RecipeCode, TargetCycles = count })).Error);
        Assert.Empty(await test.Db.ProductionTasks.ToListAsync());
    }

    [Fact]
    public async Task StaleScannerCannotRepeatACommittedCycle()
    {
        await using var test = await ProductionTestContext.CreateAsync(4);
        var started = await test.Service.StartAsync(test.Token, new StartProductionRequest { RequestId = Guid.NewGuid().ToString("N"), CharacterId = test.First.Id, RecipeCode = RecipeCode });
        var task = started.Response!.ActiveTask!;
        await using var staleDb = test.NewDbContext();
        await staleDb.ProductionTasks.FindAsync(task.Id);
        await staleDb.CharacterItemStacks.Where(item => item.CharacterId == test.First.Id).LoadAsync();
        Assert.Null(await test.Service.AdvanceDueAsync(task.Id, task.NextCycleAtUtc));
        Assert.Equal("ConcurrencyConflict", await test.NewService(staleDb).AdvanceDueAsync(task.Id, task.NextCycleAtUtc));
        await using var verification = test.NewDbContext();
        Assert.Equal(1, (await verification.ProductionTasks.SingleAsync()).CompletedCycles);
        Assert.Equal(1, (await verification.CharacterItemStacks.SingleAsync(item => item.CharacterId == test.First.Id && item.ItemCode == RecipeCode)).Quantity);
    }

    [Fact]
    public async Task GetSettlesElapsedTaskBeforeReturningInventory()
    {
        await using var test = await ProductionTestContext.CreateAsync(4);
        var started = await test.Service.StartAsync(test.Token, new StartProductionRequest { RequestId = Guid.NewGuid().ToString("N"), CharacterId = test.First.Id, RecipeCode = RecipeCode, TargetCycles = 1 });
        var task = await test.Db.ProductionTasks.FindAsync(started.Response!.ActiveTask!.Id);
        task!.StartedAtUtc = DateTime.UtcNow.AddSeconds(-20);
        task.NextCycleAtUtc = task.StartedAtUtc.AddSeconds(10);
        task.EndsAtUtc = task.NextCycleAtUtc;
        await test.Db.SaveChangesAsync();
        var view = await test.Service.GetAsync(test.Token);
        Assert.Null(view.Error);
        Assert.Null(view.Response!.ActiveTask);
        Assert.Equal(1, view.Response.Recipes.Single().CharacterQuantity);
        Assert.Equal(2, view.Response.Recipes.Single().Ingredients.Single().CharacterQuantity);
    }

    [Fact]
    public async Task TrackedOutputStackIsReusedDuringCombinedTransaction()
    {
        await using var test = await ProductionTestContext.CreateAsync(4);
        var started = await test.Service.StartAsync(test.Token, new StartProductionRequest { RequestId = Guid.NewGuid().ToString("N"), CharacterId = test.First.Id, RecipeCode = RecipeCode });
        var task = started.Response!.ActiveTask!;
        test.Db.CharacterItemStacks.Add(new CharacterItemStack { CharacterId = test.First.Id, ItemCode = RecipeCode, Quantity = 2 });
        await test.Service.SettleCharacterTrackedAsync(test.First.Id, task.NextCycleAtUtc);
        Assert.Single(test.Db.CharacterItemStacks.Local.Where(item => item.CharacterId == test.First.Id && item.ItemCode == RecipeCode));
        await test.Db.SaveChangesAsync();
        Assert.Equal(3, (await test.Db.CharacterItemStacks.SingleAsync(item => item.CharacterId == test.First.Id && item.ItemCode == RecipeCode)).Quantity);
    }

    [Fact]
    public async Task RecipeUnlockBelongsToCharacterAndMaterialsAreSpentAtSettlement()
    {
        await using var test = await ProductionTestContext.CreateAsync(5, 2);
        var (firstView, firstError) = await test.Service.GetAsync(test.Token);
        Assert.Null(firstError);
        var recipe = Assert.Single(firstView!.Recipes);
        Assert.True(recipe.IsUnlocked);
        Assert.Equal("小型治疗药水", recipe.OutputName);
        Assert.Contains("恢复 20 HP", recipe.OutputDescription);

        var (started, startError) = await test.Service.StartAsync(test.Token,
            new StartProductionRequest { RequestId = Guid.NewGuid().ToString("N"), CharacterId = test.First.Id, RecipeCode = RecipeCode });
        Assert.Null(startError);
        var firstTask = started!.ActiveTask!;
        Assert.Equal(TimeSpan.FromHours(12), firstTask.EndsAtUtc - firstTask.StartedAtUtc);
        Assert.Equal(5, (await test.Db.CharacterItemStacks.SingleAsync(item =>
            item.CharacterId == test.First.Id && item.ItemCode == HerbCode)).Quantity);
        Assert.Empty(await test.Db.CharacterActivities.ToListAsync());
        var (_, busyError) = await test.Service.StartAsync(test.Token,
            new StartProductionRequest { RequestId = Guid.NewGuid().ToString("N"), CharacterId = test.First.Id, RecipeCode = RecipeCode });
        Assert.Equal("CharacterBusy", busyError);

        test.Owner.ActiveCharacterId = test.Second.Id;
        test.Owner.Version++;
        await test.Db.SaveChangesAsync();
        var (_, lockedError) = await test.Service.StartAsync(test.Token,
            new StartProductionRequest { RequestId = Guid.NewGuid().ToString("N"), CharacterId = test.Second.Id, RecipeCode = RecipeCode });
        Assert.Equal("RecipeLocked", lockedError);
        test.Db.CharacterBattleMilestones.Add(new CharacterBattleMilestone
        {
            CharacterId = test.Second.Id, Kind = BattleMilestoneService.MonsterKillKind,
            TargetCode = "tirisfal-dusk-bat", Count = 1,
            FirstAtUtc = DateTime.UtcNow, LastAtUtc = DateTime.UtcNow
        });
        await test.Db.SaveChangesAsync();
        var (secondStarted, secondError) = await test.Service.StartAsync(test.Token,
            new StartProductionRequest { RequestId = Guid.NewGuid().ToString("N"), CharacterId = test.Second.Id, RecipeCode = RecipeCode });
        Assert.Null(secondError);
        Assert.NotNull(secondStarted!.ActiveTask);

        Assert.Null(await test.Service.AdvanceDueAsync(firstTask.Id, firstTask.StartedAtUtc.AddSeconds(21)));
        Assert.Equal(1, (await test.Db.CharacterItemStacks.SingleAsync(item =>
            item.CharacterId == test.First.Id && item.ItemCode == HerbCode)).Quantity);
        Assert.Equal(2, (await test.Db.CharacterItemStacks.SingleAsync(item =>
            item.CharacterId == test.First.Id && item.ItemCode == RecipeCode)).Quantity);
        Assert.Null(await test.Service.AdvanceDueAsync(secondStarted.ActiveTask!.Id,
            secondStarted.ActiveTask.NextCycleAtUtc));
        Assert.Equal("Running", (await test.Db.ProductionTasks.FindAsync(secondStarted.ActiveTask.Id))!.Status);
        Assert.Equal(1, (await test.Db.CharacterItemStacks.SingleAsync(item =>
            item.CharacterId == test.Second.Id && item.ItemCode == RecipeCode)).Quantity);
        Assert.Null(await test.Service.AdvanceDueAsync(firstTask.Id, firstTask.StartedAtUtc.AddSeconds(31)));
        Assert.Equal("MaterialShortage", (await test.Db.ProductionTasks.FindAsync(firstTask.Id))!.Status);
        Assert.Null(await test.Service.AdvanceDueAsync(secondStarted.ActiveTask.Id,
            secondStarted.ActiveTask.NextCycleAtUtc.AddSeconds(10)));
        Assert.Equal("MaterialShortage", (await test.Db.ProductionTasks.FindAsync(secondStarted.ActiveTask.Id))!.Status);
        Assert.Empty(await test.Db.CharacterActivities.ToListAsync());
        Assert.Equal(2, (await test.Db.CharacterItemStacks.SingleAsync(item =>
            item.CharacterId == test.First.Id && item.ItemCode == RecipeCode)).Quantity);
    }

    [Fact]
    public async Task StopBeforeFirstCycleDoesNotReserveOrSpendMaterials()
    {
        await using var test = await ProductionTestContext.CreateAsync(2);
        var (view, _) = await test.Service.StartAsync(test.Token,
            new StartProductionRequest { RequestId = Guid.NewGuid().ToString("N"), CharacterId = test.First.Id, RecipeCode = RecipeCode });
        var (stopped, error) = await test.Service.StopAsync(test.Token, view!.ActiveTask!.Id);
        Assert.Null(error);
        Assert.Null(stopped!.ActiveTask);
        Assert.Equal("Stopped", stopped.RecentTasks.Single().Status);
        Assert.Equal(0, stopped.RecentTasks.Single().TotalQuantity);
        Assert.Equal(2, (await test.Db.CharacterItemStacks.SingleAsync(item =>
            item.CharacterId == test.First.Id && item.ItemCode == HerbCode)).Quantity);
        Assert.Empty(await test.Db.CharacterActivities.ToListAsync());
    }

    [Fact]
    public async Task TwelveHourCatchUpAndRepeatedScanCannotDoubleProduce()
    {
        await using var test = await ProductionTestContext.CreateAsync(9000);
        var (view, error) = await test.Service.StartAsync(test.Token,
            new StartProductionRequest { RequestId = Guid.NewGuid().ToString("N"), CharacterId = test.First.Id, RecipeCode = RecipeCode });
        Assert.Null(error);
        var task = view!.ActiveTask!;
        Assert.Null(await test.Service.AdvanceDueAsync(task.Id, task.EndsAtUtc.AddHours(1)));
        Assert.Null(await test.Service.AdvanceDueAsync(task.Id, task.EndsAtUtc.AddHours(2)));
        var completed = await test.Db.ProductionTasks.SingleAsync();
        Assert.Equal("Completed", completed.Status);
        Assert.Equal((4320, 4320), (completed.CompletedCycles, completed.TotalQuantity));
        Assert.Equal(360, (await test.Db.CharacterItemStacks.SingleAsync(item =>
            item.CharacterId == test.First.Id && item.ItemCode == HerbCode)).Quantity);
        Assert.Equal(4320, (await test.Db.CharacterItemStacks.SingleAsync(item =>
            item.CharacterId == test.First.Id && item.ItemCode == RecipeCode)).Quantity);
        Assert.Empty(await test.Db.CharacterActivities.ToListAsync());
    }

    [Fact]
    public async Task CharactersWithSeparateBagsCanProduceAtTheSameTime()
    {
        await using var test = await ProductionTestContext.CreateAsync(2, 2);
        test.Db.CharacterBattleMilestones.Add(new CharacterBattleMilestone
        {
            CharacterId = test.Second.Id, Kind = BattleMilestoneService.MonsterKillKind,
            TargetCode = "northshire-wolves", Count = 1,
            FirstAtUtc = DateTime.UtcNow, LastAtUtc = DateTime.UtcNow
        });
        await test.Db.SaveChangesAsync();
        var (first, _) = await test.Service.StartAsync(test.Token,
            new StartProductionRequest { RequestId = Guid.NewGuid().ToString("N"), CharacterId = test.First.Id, RecipeCode = RecipeCode });
        test.Owner.ActiveCharacterId = test.Second.Id;
        test.Owner.Version++;
        await test.Db.SaveChangesAsync();
        var (second, secondError) = await test.Service.StartAsync(test.Token,
            new StartProductionRequest { RequestId = Guid.NewGuid().ToString("N"), CharacterId = test.Second.Id, RecipeCode = RecipeCode });
        Assert.Null(secondError);

        await using var firstDb = test.NewDbContext();
        await using var secondDb = test.NewDbContext();
        var firstService = test.NewService(firstDb);
        var secondService = test.NewService(secondDb);
        Assert.Null(await firstService.AdvanceDueAsync(first!.ActiveTask!.Id, first.ActiveTask.NextCycleAtUtc));
        Assert.Null(await secondService.AdvanceDueAsync(
            second!.ActiveTask!.Id, second.ActiveTask.NextCycleAtUtc));

        await using var verificationDb = test.NewDbContext();
        Assert.Equal(2, await verificationDb.CharacterItemStacks.CountAsync(item =>
            item.ItemCode == RecipeCode && item.Quantity == 1));
        Assert.Equal(0, (await verificationDb.CharacterItemStacks.SingleAsync(item =>
            item.CharacterId == test.First.Id && item.ItemCode == HerbCode)).Quantity);
        Assert.Equal(0, (await verificationDb.CharacterItemStacks.SingleAsync(item =>
            item.CharacterId == test.Second.Id && item.ItemCode == HerbCode)).Quantity);
    }

    [Fact]
    public async Task AnotherCharactersMaterialsCannotStartProduction()
    {
        await using var test = await ProductionTestContext.CreateAsync(0, 4);
        var (_, shortage) = await test.Service.StartAsync(test.Token,
            new StartProductionRequest { RequestId = Guid.NewGuid().ToString("N"), CharacterId = test.First.Id, RecipeCode = RecipeCode });
        Assert.Equal("InsufficientMaterials", shortage);
        test.Owner.ActiveCharacterId = test.Second.Id;
        test.Owner.Version++;
        test.Db.CharacterBattleMilestones.Add(new CharacterBattleMilestone
        {
            CharacterId = test.Second.Id, Kind = BattleMilestoneService.MonsterKillKind,
            TargetCode = "northshire-wolves", Count = 1,
            FirstAtUtc = DateTime.UtcNow, LastAtUtc = DateTime.UtcNow
        });
        await test.Db.SaveChangesAsync();
        var (started, startError) = await test.Service.StartAsync(test.Token,
            new StartProductionRequest { RequestId = Guid.NewGuid().ToString("N"), CharacterId = test.Second.Id, RecipeCode = RecipeCode });
        Assert.Null(startError);
        Assert.NotNull(started!.ActiveTask);
        Assert.Equal(4, started.Recipes.Single().Ingredients.Single().CharacterQuantity);
    }

    private sealed class ProductionTestContext : IAsyncDisposable
    {
        private readonly string _path;
        private readonly ProductionCatalog _catalog;
        private readonly WorldCatalog _world;
        private readonly MaterialCatalog _materials;
        private readonly ConsumableCatalog _consumables;
        private ProductionTestContext(string path, GameDbContext db, User owner, Character first,
            Character second, ProductionCatalog catalog, WorldCatalog world,
            MaterialCatalog materials, ConsumableCatalog consumables)
        {
            _path = path;
            Db = db;
            Owner = owner;
            First = first;
            Second = second;
            _catalog = catalog;
            _world = world;
            _materials = materials;
            _consumables = consumables;
            Service = NewService(db);
        }

        public GameDbContext Db { get; }
        public User Owner { get; }
        public Character First { get; }
        public Character Second { get; }
        public ProductionService Service { get; }
        public string Token => "production-owner-token";

        public GameDbContext NewDbContext() => new(new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite($"Data Source={_path};Pooling=False").Options);

        public ProductionService NewService(GameDbContext db, ProfessionCatalog? professions = null) => new(db,
            new UserService(db, ProgressionTestFactory.Create(), SkillTestFactory.Create()),
            _catalog, _world, _materials, _consumables,
            Options.Create(new ActivityOptions { MaximumHours = 12 }), professions);

        public GatheringService NewGatheringService()
        {
            var gathering = new GatheringCatalog(Options.Create(new GatheringOptions
            {
                Points = [new GatheringPointOptions
                {
                    Code = "elwynn-peacebloom", Name = "岩芽宁神花", RegionCode = "elwynn",
                    MaterialCode = HerbCode, CycleSeconds = 20, OutputQuantity = 1,
                    UnlockKind = BattleMilestoneService.MonsterKillKind,
                    UnlockTargetCode = "northshire-wolves"
                }]
            }), _world, _materials);
            return new GatheringService(Db,
                new UserService(Db, ProgressionTestFactory.Create(), SkillTestFactory.Create()),
                gathering, _world, _materials,
                Options.Create(new ActivityOptions { MaximumHours = 12 }));
        }

        public static async Task<ProductionTestContext> CreateAsync(int herbQuantity,
            int secondHerbQuantity = 0)
        {
            var path = Path.Combine(Path.GetTempPath(), $"idlegame-production-{Guid.NewGuid():N}.db");
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options);
            await db.Database.EnsureCreatedAsync();
            var owner = new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 };
            var first = new Character { Id = 1, UserId = 1, Name = "First", Level = 1, Hp = 100, MaxHp = 100, Attack = 20 };
            var second = new Character { Id = 2, UserId = 1, Name = "Second", Level = 1, Hp = 100, MaxHp = 100, Attack = 20 };
            db.AddRange(owner, first, second,
                new CharacterBattleMilestone
                {
                    CharacterId = first.Id, Kind = BattleMilestoneService.MonsterKillKind,
                    TargetCode = "northshire-wolves", Count = 1,
                    FirstAtUtc = DateTime.UtcNow, LastAtUtc = DateTime.UtcNow
                },
                new CharacterItemStack { CharacterId = first.Id, ItemCode = HerbCode, Quantity = herbQuantity },
                new CharacterItemStack { CharacterId = second.Id, ItemCode = HerbCode, Quantity = secondHerbQuantity },
                new UserLoginSession
                {
                    UserId = owner.Id, Token = "production-owner-token",
                    CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1)
                });
            await db.SaveChangesAsync();
            var world = WorldCatalog.LoadDefault();
            var materials = new MaterialCatalog(Options.Create(new MaterialOptions
            {
                Items = [new MaterialItemOptions
                {
                    Code = HerbCode, Name = "宁神花", Description = "测试材料"
                }]
            }));
            var consumables = new ConsumableCatalog(Options.Create(new ConsumableOptions
            {
                Items = [new ConsumableItemOptions
                {
                    Code = RecipeCode, Name = "小型治疗药水",
                    HealAmount = 20, CooldownRounds = 3, CooldownGroup = "healing"
                }]
            }));
            var catalog = new ProductionCatalog(Options.Create(new ProductionOptions
            {
                Recipes = [new ProductionRecipeOptions
                {
                    Code = RecipeCode, Name = "小型治疗药水", OutputCode = RecipeCode,
                    CycleSeconds = 10, OutputQuantity = 1,
                    UnlockKind = BattleMilestoneService.MonsterKillKind,
                    UnlockTargetCode = "northshire-wolves",
                    AlternativeUnlockTargetCodes = ["tirisfal-dusk-bat"],
                    Ingredients = [new ProductionIngredientOptions { Code = HerbCode, Quantity = 2 }]
                }]
            }), world, materials, consumables);
            return new ProductionTestContext(path, db, owner, first, second,
                catalog, world, materials, consumables);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            File.Delete(_path);
        }
    }
}
