using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Production;
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
    public async Task AlchemyLevelsIndependentlyAndIngredientSavingUsesTaskSnapshot()
    {
        await using var test = await ProductionTestContext.CreateAsync(4);
        var catalog = ProfessionTestFactory.Create();
        var service = test.NewService(test.Db, catalog);
        var first = await service.StartAsync(test.Token,
            new StartProductionRequest { CharacterId = test.First.Id, RecipeCode = RecipeCode });
        Assert.Null(first.Error);
        var initial = first.Response!.ActiveTask!;
        Assert.Null(await service.AdvanceDueAsync(initial.Id, initial.StartedAtUtc.AddSeconds(20)));
        Assert.Equal(2, test.First.AlchemyLevel);
        Assert.Equal(1, test.First.AlchemyTalentPoints);
        Assert.Equal(1, test.Second.AlchemyLevel);
        Assert.Null((await service.StopAsync(test.Token, initial.Id)).Error);

        catalog.GrantExperience(test.First, ProfessionCatalog.AlchemyCode, 10);

        var talents = new ProfessionService(test.Db,
            new UserService(test.Db, ProgressionTestFactory.Create(), SkillTestFactory.Create()), catalog);
        Assert.Null((await talents.SpendAsync(test.Token, ProfessionCatalog.AlchemyCode, "alchemy-save")).Error);
        Assert.Null((await talents.SpendAsync(test.Token, ProfessionCatalog.AlchemyCode, "alchemy-yield")).Error);
        var herb = await test.Db.CharacterItemStacks.SingleAsync(item =>
            item.CharacterId == test.First.Id && item.ItemCode == HerbCode);
        herb.Quantity = 3;
        herb.Version++;
        await test.Db.SaveChangesAsync();
        var second = await service.StartAsync(test.Token,
            new StartProductionRequest { CharacterId = test.First.Id, RecipeCode = RecipeCode });
        Assert.Null(second.Error);
        var enhanced = second.Response!.ActiveTask!;
        Assert.Equal(100, (await test.Db.ProductionTasks.FindAsync(enhanced.Id))!.IngredientSaveChancePercent);
        Assert.Equal(100, (await test.Db.ProductionTasks.FindAsync(enhanced.Id))!.ExtraYieldChancePercent);
        Assert.Null((await talents.ResetAsync(test.Token, ProfessionCatalog.AlchemyCode)).Error);
        Assert.Null(await service.AdvanceDueAsync(enhanced.Id, enhanced.StartedAtUtc.AddSeconds(20)));
        var completed = (await test.Db.ProductionTasks.FindAsync(enhanced.Id))!;
        Assert.Equal(4, completed.TotalQuantity);
        Assert.Equal(2, completed.ExtraYieldQuantity);
        Assert.Equal(2, completed.SavedIngredientQuantity);
        Assert.Equal(1, herb.Quantity);
    }

    [Fact]
    public async Task GatheredHerbsCanBeUsedForAlchemyWithoutAnyTransfer()
    {
        await using var test = await ProductionTestContext.CreateAsync(0);
        var gathering = test.NewGatheringService();
        var (harvest, harvestError) = await gathering.StartAsync(test.Token,
            new Game.Shared.Dtos.Gathering.StartGatheringRequest
            {
                CharacterId = test.First.Id, PointCode = "elwynn-peacebloom"
            });
        Assert.Null(harvestError);
        var gatheringTask = harvest!.ActiveTask!;
        Assert.Null(await gathering.AdvanceDueAsync(gatheringTask.Id,
            gatheringTask.StartedAtUtc.AddSeconds(40)));
        Assert.Equal(2, (await test.Db.CharacterItemStacks.SingleAsync(item =>
            item.CharacterId == test.First.Id && item.ItemCode == HerbCode)).Quantity);
        Assert.Equal("CharacterBusy", (await test.Service.StartAsync(test.Token,
            new StartProductionRequest { CharacterId = test.First.Id, RecipeCode = RecipeCode })).Error);
        Assert.Null((await gathering.StopAsync(test.Token, gatheringTask.Id)).Error);

        var (started, startError) = await test.Service.StartAsync(test.Token,
            new StartProductionRequest { CharacterId = test.First.Id, RecipeCode = RecipeCode });
        Assert.Null(startError);
        Assert.Null(await test.Service.AdvanceDueAsync(started!.ActiveTask!.Id,
            started.ActiveTask.NextCycleAtUtc));
        Assert.Equal(0, (await test.Db.CharacterItemStacks.SingleAsync(item =>
            item.CharacterId == test.First.Id && item.ItemCode == HerbCode)).Quantity);
        Assert.Equal(1, (await test.Db.CharacterItemStacks.SingleAsync(item =>
            item.CharacterId == test.First.Id && item.ItemCode == RecipeCode)).Quantity);
        Assert.False(await test.Db.CharacterItemStacks.AnyAsync(item =>
            item.CharacterId == test.Second.Id && item.Quantity > 0));
    }

    [Fact]
    public async Task RecipeUnlockBelongsToCharacterAndMaterialsAreSpentAtSettlement()
    {
        await using var test = await ProductionTestContext.CreateAsync(5, 2);
        var (firstView, firstError) = await test.Service.GetAsync(test.Token);
        Assert.Null(firstError);
        Assert.True(Assert.Single(firstView!.Recipes).IsUnlocked);

        var (started, startError) = await test.Service.StartAsync(test.Token,
            new StartProductionRequest { CharacterId = test.First.Id, RecipeCode = RecipeCode });
        Assert.Null(startError);
        var firstTask = started!.ActiveTask!;
        Assert.Equal(TimeSpan.FromHours(12), firstTask.EndsAtUtc - firstTask.StartedAtUtc);
        Assert.Equal(5, (await test.Db.CharacterItemStacks.SingleAsync(item =>
            item.CharacterId == test.First.Id && item.ItemCode == HerbCode)).Quantity);
        Assert.Equal(CharacterActivityManager.ProductionKind,
            (await test.Db.CharacterActivities.SingleAsync()).Kind);
        var (_, busyError) = await test.Service.StartAsync(test.Token,
            new StartProductionRequest { CharacterId = test.First.Id, RecipeCode = RecipeCode });
        Assert.Equal("CharacterBusy", busyError);

        test.Owner.ActiveCharacterId = test.Second.Id;
        test.Owner.Version++;
        await test.Db.SaveChangesAsync();
        var (_, lockedError) = await test.Service.StartAsync(test.Token,
            new StartProductionRequest { CharacterId = test.Second.Id, RecipeCode = RecipeCode });
        Assert.Equal("RecipeLocked", lockedError);
        test.Db.CharacterBattleMilestones.Add(new CharacterBattleMilestone
        {
            CharacterId = test.Second.Id, Kind = BattleMilestoneService.MonsterKillKind,
            TargetCode = "northshire-wolves", Count = 1,
            FirstAtUtc = DateTime.UtcNow, LastAtUtc = DateTime.UtcNow
        });
        await test.Db.SaveChangesAsync();
        var (secondStarted, secondError) = await test.Service.StartAsync(test.Token,
            new StartProductionRequest { CharacterId = test.Second.Id, RecipeCode = RecipeCode });
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
            new StartProductionRequest { CharacterId = test.First.Id, RecipeCode = RecipeCode });
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
            new StartProductionRequest { CharacterId = test.First.Id, RecipeCode = RecipeCode });
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
            new StartProductionRequest { CharacterId = test.First.Id, RecipeCode = RecipeCode });
        test.Owner.ActiveCharacterId = test.Second.Id;
        test.Owner.Version++;
        await test.Db.SaveChangesAsync();
        var (second, secondError) = await test.Service.StartAsync(test.Token,
            new StartProductionRequest { CharacterId = test.Second.Id, RecipeCode = RecipeCode });
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
            new StartProductionRequest { CharacterId = test.First.Id, RecipeCode = RecipeCode });
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
            new StartProductionRequest { CharacterId = test.Second.Id, RecipeCode = RecipeCode });
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
                    Code = "elwynn-peacebloom", Name = "北郡宁神花", RegionCode = "elwynn",
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
