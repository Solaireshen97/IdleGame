using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Planting;
using Game.Shared.Dtos.Production;
using Game.Shared.Dtos.Shop;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class ParallelLogisticsIntegrationTests
{
    [Fact]
    public async Task BattleCharacterCanBuyPlantHarvestAndCompleteThreeBatchesWithoutSharingResources()
    {
        await using var test = await LogisticsFixture.CreateAsync();
        var purchase = new PurchaseShopItemRequest { CharacterId = 1, Code = test.Plant.SeedCode, Quantity = 2, RequestId = Guid.NewGuid().ToString() };
        Assert.Null((await test.Shop.PurchaseAsync(test.Token, purchase)).Error);
        purchase.RequestId = Guid.Parse(purchase.RequestId).ToString("N");
        Assert.Null((await test.Shop.PurchaseAsync(test.Token, purchase)).Error);
        Assert.Equal(1000 - 2 * test.Plant.SeedPrice, (await test.Db.Characters.FindAsync(1))!.Gold);
        Assert.Equal(2, await test.QuantityAsync(test.Plant.SeedCode));

        var harvest = await test.PlantAndMatureAsync(2);
        Assert.Null((await test.Garden.HarvestAsync(test.Token, harvest)).Error);
        Assert.Equal(2 * test.Plant.HarvestQuantity, await test.QuantityAsync(test.Plant.MaterialCode));
        var start = await test.Production.StartAsync(test.Token, new StartProductionRequest
        {
            CharacterId = 1, RecipeCode = test.Recipe.Code, TargetCycles = 3, RequestId = Guid.NewGuid().ToString()
        });
        Assert.Null(start.Error);
        var task = await test.Db.ProductionTasks.SingleAsync();
        Assert.Null(await test.Production.AdvanceDueAsync(task.Id, task.EndsAtUtc.AddSeconds(1)));
        Assert.Equal("Completed", task.Status);
        Assert.Equal(3, task.CompletedCycles);
        Assert.Equal(3 * test.Recipe.OutputQuantity, await test.QuantityAsync(test.Recipe.OutputCode));
        Assert.Equal(2 * test.Plant.HarvestQuantity - 3 * test.Recipe.Ingredients.Single().Quantity,
            await test.QuantityAsync(test.Plant.MaterialCode));
        Assert.Null((await test.Garden.HarvestAsync(test.Token, harvest)).Response);
        await test.AssertBattleAndIsolationAsync();
    }

    [Fact]
    public async Task HarvestStopsOverdueAlchemyBeforeFreshHerbsCanPayForPastBatches()
    {
        await using var test = await LogisticsFixture.CreateAsync();
        test.Db.CharacterItemStacks.Add(new() { CharacterId = 1, ItemCode = test.Plant.SeedCode, Quantity = 1 });
        var ingredient = test.Recipe.Ingredients.Single();
        test.Db.CharacterItemStacks.Add(new() { CharacterId = 1, ItemCode = ingredient.Code, Quantity = ingredient.Quantity });
        await test.Db.SaveChangesAsync();
        var harvest = await test.PlantAndMatureAsync(1);
        Assert.Null((await test.Production.StartAsync(test.Token, new StartProductionRequest
        {
            CharacterId = 1, RecipeCode = test.Recipe.Code, TargetCycles = 3, RequestId = Guid.NewGuid().ToString()
        })).Error);
        var task = await test.Db.ProductionTasks.SingleAsync();
        task.StartedAtUtc = DateTime.UtcNow.AddMinutes(-1);
        task.NextCycleAtUtc = task.StartedAtUtc.AddSeconds(task.CycleSeconds);
        task.EndsAtUtc = task.StartedAtUtc.AddSeconds(3 * task.CycleSeconds);
        await test.Db.SaveChangesAsync();
        // Persisted dates simulate returning after being offline; harvesting is the real settlement trigger.
        Assert.Null((await test.Garden.HarvestAsync(test.Token, harvest)).Error);
        Assert.Equal("MaterialShortage", task.Status);
        Assert.Equal(1, task.CompletedCycles);
        Assert.Equal(test.Recipe.OutputQuantity, await test.QuantityAsync(test.Recipe.OutputCode));
        Assert.Equal(test.Plant.HarvestQuantity, await test.QuantityAsync(ingredient.Code));
        Assert.Null((await test.Production.GetAsync(test.Token)).Error);
        Assert.Equal(test.Recipe.OutputQuantity, await test.QuantityAsync(test.Recipe.OutputCode));
        Assert.Equal(test.Plant.HarvestQuantity, await test.QuantityAsync(ingredient.Code));
        await test.AssertBattleAndIsolationAsync();
    }

    private sealed class LogisticsFixture : IAsyncDisposable
    {
        public const int BattleSource = 777;
        public string Token => "parallel-logistics-token";
        public required GameDbContext Db { get; init; }
        public required ShopService Shop { get; init; }
        public required PlantingService Garden { get; init; }
        public required ProductionService Production { get; init; }
        public required PlantOptions Plant { get; init; }
        public required ProductionRecipeOptions Recipe { get; init; }

        public async Task<int> QuantityAsync(string code) => await Db.CharacterItemStacks.Where(s => s.CharacterId == 1 && s.ItemCode == code).Select(s => s.Quantity).SingleOrDefaultAsync();
        public async Task<HarvestGardenRequest> PlantAndMatureAsync(int count)
        {
            var garden = await Garden.GetAsync(Token);
            Assert.Null(garden.Error);
            var selected = garden.Response!.Plots.Take(count).Select(p => new PlotVersionRequest(p.PlotIndex, p.Version)).ToList();
            var planted = await Garden.PlantAsync(Token, new(1, Guid.NewGuid().ToString(), Plant.Code, selected));
            Assert.Null(planted.Error);
            var plots = await Db.CharacterGardenPlots.Where(p => p.CharacterId == 1 && p.PlantCode != null).ToListAsync();
            foreach (var plot in plots) { plot.PlantedAtUtc = DateTime.UtcNow.AddHours(-12); plot.MaturesAtUtc = DateTime.UtcNow.AddHours(-1); }
            await Db.SaveChangesAsync();
            return new(1, plots.Select(p => new PlotVersionRequest(p.PlotIndex, p.Version)).ToList());
        }
        public async Task AssertBattleAndIsolationAsync()
        {
            var activity = await Db.CharacterActivities.SingleAsync();
            Assert.Equal(1, activity.CharacterId); Assert.Equal("Battle", activity.Kind); Assert.Equal(BattleSource, activity.SourceId);
            Assert.Empty(await Db.CharacterItemStacks.Where(s => s.CharacterId == 2).ToListAsync());
            Assert.Empty(await Db.CharacterGardenPlots.Where(p => p.CharacterId == 2).ToListAsync());
            Assert.Empty(await Db.ProductionTasks.Where(p => p.CharacterId == 2).ToListAsync());
            var first = (await Db.Characters.FindAsync(1))!;
            var second = (await Db.Characters.FindAsync(2))!;
            Assert.Equal(1, first.GatheringLevel); Assert.Equal(1, first.AlchemyLevel);
            Assert.Equal(0, first.GatheringExperience); Assert.Equal(0, first.AlchemyExperience);
            Assert.Equal(0, first.GatheringTalentPoints); Assert.Equal(0, first.AlchemyTalentPoints);
            Assert.Equal(500, second.Gold);
        }
        public static async Task<LogisticsFixture> CreateAsync()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "Game.Server", "appsettings.json"))) root = root.Parent;
            Assert.NotNull(root);
            var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(root!.FullName, "Game.Server", "appsettings.json")).Build();
            IOptions<T> Settings<T>(string section) where T : class => Options.Create(config.GetSection(section).Get<T>()!);
            var world = WorldCatalog.LoadDefault();
            var weapons = new WeaponCatalog(Settings<WeaponOptions>(WeaponOptions.SectionName));
            var materials = new MaterialCatalog(Settings<MaterialOptions>(MaterialOptions.SectionName));
            var consumables = new ConsumableCatalog(Settings<ConsumableOptions>(ConsumableOptions.SectionName), weapons);
            var plants = new PlantingCatalog(Settings<PlantingOptions>(PlantingOptions.SectionName));
            var recipes = new ProductionCatalog(Settings<ProductionOptions>(ProductionOptions.SectionName), world, materials, consumables);
            var souls = new SoulImprintCatalog(Settings<SoulImprintOptions>(SoulImprintOptions.SectionName));
            var exchanges = new DungeonExchangeCatalog(Settings<DungeonExchangeOptions>(DungeonExchangeOptions.SectionName), materials, weapons, souls);
            var shopCatalog = new ShopCatalog(Settings<ShopOptions>(ShopOptions.SectionName), consumables, weapons, plants, materials);
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite("Data Source=:memory:").Options);
            await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync();
            var plant = plants.FindPlant("peacebloom")!;
            var recipe = recipes.FindRecipe("minor-healing-potion")!;
            db.AddRange(new User { Id = 1, UserName = "logistics", PasswordHash = "x", ActiveCharacterId = 1 },
                new Character { Id = 1, UserId = 1, Name = "Battle gardener", Level = 9, Gold = 1000, Hp = 100, MaxHp = 100, Attack = 20 },
                new Character { Id = 2, UserId = 1, Name = "Other character", Level = 9, Gold = 500, Hp = 100, MaxHp = 100, Attack = 20 },
                new CharacterActivity { CharacterId = 1, Kind = "Battle", SourceId = BattleSource, StartedAtUtc = DateTime.UtcNow },
                new CharacterBattleMilestone { CharacterId = 1, Kind = plant.UnlockKind, TargetCode = plant.UnlockTargetCode, Count = 1, FirstAtUtc = DateTime.UtcNow, LastAtUtc = DateTime.UtcNow },
                new UserLoginSession { UserId = 1, Token = "parallel-logistics-token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();
            var users = new UserService(db, ProgressionTestFactory.Create(), SkillTestFactory.Create());
            var production = new ProductionService(db, users, recipes, world, materials, consumables, Settings<ActivityOptions>(ActivityOptions.SectionName));
            return new() { Db = db, Plant = plant, Recipe = recipe, Production = production,
                Garden = new(db, users, plants, production, world),
                Shop = new(db, users, shopCatalog, consumables, weapons, materials, exchanges, souls, production: production, plants: plants, world: world) };
        }
        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }
}
