using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Planting;
using Game.Shared.Dtos.Production;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class AlchemyProgressionBalanceTests
{
    [Theory]
    [InlineData("elwynn")]
    [InlineData("tirisfal")]
    [InlineData("durotar")]
    [InlineData("dun-morogh")]
    [InlineData("mulgore")]
    [InlineData("eversong")]
    public async Task FirstHuntThenEntryThenDeepUnlockCorrespondingSeedsAndRecipes(string region)
    {
        var content = new Content();
        await using var db = await CreateDbAsync();
        var character = await db.Characters.SingleAsync();
        var users = new UserService(db, ProgressionTestFactory.Create(), SkillTestFactory.Create());
        var production = new ProductionService(db, users, content.Production, content.World, content.Materials,
            content.Consumables, Options.Create(new ActivityOptions()));
        var garden = new PlantingService(db, users, content.Plants, production, content.World);
        var shop = new ShopService(db, users, content.Shop, content.Consumables, content.Weapons, content.Materials,
            content.Exchanges, content.Souls, plants: content.Plants, world: content.World);
        var firstHunt = content.World.Dungeons.Single(d => d.IsVisible && d.RegionCode == region && d.DungeonKind == "Hunt" && d.MinimumLevel == 1);
        var common = content.Plants.Plants.Single(p => p.RegionCode == region && !p.IsRare);
        var rare = content.Plants.Plants.Single(p => p.RegionCode == region && p.IsRare);
        var normalRecipe = content.Production.Recipes.Single(r => !r.OutputCode.StartsWith("greater-") && r.Ingredients.Any(i => i.Code == common.MaterialCode));
        var rareRecipe = content.Production.Recipes.Single(r => r.Ingredients.Any(i => i.Code == rare.MaterialCode));
        foreach (var code in new[] { "peacebloom", common.MaterialCode, rare.MaterialCode })
            db.CharacterItemStacks.Add(new() { CharacterId = 1, ItemCode = code, Quantity = 500 });
        db.CharacterItemStacks.AddRange(new CharacterItemStack { CharacterId = 1, ItemCode = "seed-peacebloom", Quantity = 1 },
            new CharacterItemStack { CharacterId = 1, ItemCode = common.SeedCode, Quantity = 1 });
        await db.SaveChangesAsync();
        Assert.False((await garden.GetAsync("token")).Response!.Plants.Single(p => p.Code == "peacebloom").IsUnlocked);
        Assert.Equal("RecipeLocked", (await production.StartAsync("token", Request("minor-healing-potion"))).Error);
        var milestones = new BattleMilestoneService(db);
        await milestones.RecordAsync([1], "MonsterKill", firstHunt.Code, DateTime.UtcNow);
        await db.SaveChangesAsync();
        Assert.True((await garden.GetAsync("token")).Response!.Plants.Single(p => p.Code == "peacebloom").IsUnlocked);
        Assert.True((await shop.GetAsync("token")).Response!.Items.Single(p => p.Code == "seed-peacebloom").IsUnlocked);
        Assert.Null((await garden.PlantAsync("token", new(1, Guid.NewGuid().ToString(), "peacebloom", [new(0, 0)]))).Error);
        var plot = await db.CharacterGardenPlots.SingleAsync(p => p.PlotIndex == 0);
        Assert.Equal(3, plot.HarvestQuantity);
        plot.MaturesAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        Assert.Null((await garden.HarvestAsync("token", new(1, [new(0, 1)]))).Error);
        Assert.Equal(503, (await db.CharacterItemStacks.SingleAsync(s => s.ItemCode == "peacebloom")).Quantity);
        await ProduceOneAsync("minor-healing-potion", "minor-healing-potion");

        character.Level = 10;
        var lastHunt = content.World.Dungeons.Single(d => d.IsVisible && d.RegionCode == region && d.DungeonKind == "Hunt" && d.MinimumLevel == 5);
        await milestones.RecordAsync([1], "MonsterKill", lastHunt.Code, DateTime.UtcNow);
        await db.SaveChangesAsync();
        Assert.False((await garden.GetAsync("token")).Response!.Plants.Single(p => p.Code == common.Code).IsUnlocked);
        Assert.False((await shop.GetAsync("token")).Response!.Items.Single(p => p.Code == common.SeedCode).IsUnlocked);
        Assert.Equal("RecipeLocked", (await production.StartAsync("token", Request(normalRecipe.Code))).Error);
        Assert.Equal("PlantLocked", (await garden.PlantAsync("token", new(1, Guid.NewGuid().ToString(), common.Code, [new(1, 0)]))).Error);
        await milestones.RecordAsync([1], "DungeonClear", common.UnlockTargetCode, DateTime.UtcNow);
        await db.SaveChangesAsync();
        Assert.True((await garden.GetAsync("token")).Response!.Plants.Single(p => p.Code == common.Code).IsUnlocked);
        Assert.True((await shop.GetAsync("token")).Response!.Items.Single(p => p.Code == common.SeedCode).IsUnlocked);
        Assert.Null((await garden.PlantAsync("token", new(1, Guid.NewGuid().ToString(), common.Code, [new(1, 0)]))).Error);
        var ordinary = (await production.GetAsync("token")).Response!.Recipes;
        Assert.All(ordinary.Where(r => r.OutputCode is "northshire-battle-draught" or "whetstone-oil"), r =>
        {
            Assert.True(r.IsUnlocked);
            Assert.StartsWith("通关以下任一副本", r.UnlockDescription);
        });
        await ProduceOneAsync(normalRecipe.Code, normalRecipe.OutputCode);
        Assert.Equal("RecipeLocked", (await production.StartAsync("token", Request(rareRecipe.Code))).Error);
        Assert.Empty(content.Plants.SeedDropsFor(common.UnlockTargetCode));
        await milestones.RecordAsync([1], "DungeonClear", rare.UnlockTargetCode, DateTime.UtcNow);
        await db.SaveChangesAsync();
        await ProduceOneAsync(rareRecipe.Code, rareRecipe.OutputCode);
        Assert.DoesNotContain(content.Shop.Items, item => item.Code == rare.SeedCode);

        async Task ProduceOneAsync(string recipeCode, string outputCode)
        {
            var started = await production.StartAsync("token", Request(recipeCode));
            Assert.Null(started.Error);
            var task = started.Response!.ActiveTask!;
            Assert.Null(await production.AdvanceDueAsync(task.Id, DateTime.UtcNow.AddMinutes(1)));
            Assert.Equal(1, (await db.CharacterItemStacks.SingleAsync(s => s.ItemCode == outputCode)).Quantity);
        }
    }

    [Fact]
    public void DailyProductionRemainsBelowOnePotionPerThirtyMinuteRunEvenWithContinuousPlanting()
    {
        var content = new Content();
        var fastestHerbsPerHour = content.Plants.Plants.Max(p => p.HarvestQuantity / (p.GrowthSeconds / 3600m));
        var fewestHerbsPerPotion = content.Production.Recipes.Min(r => r.Ingredients.Sum(i => i.Quantity) / (decimal)r.OutputQuantity);
        var dailyPotionUpperBound = 4 * 24 * fastestHerbsPerHour / fewestHerbsPerPotion;
        Assert.Equal(36, dailyPotionUpperBound);
        Assert.True(dailyPotionUpperBound < 24 * 60 / 30);
    }

    [Fact]
    public async Task DropPreviewUsesSelectedDepthAndShowsSeparateFirstSeedGuarantee()
    {
        var content = new Content();
        await using var db = await CreateDbAsync();
        var service = new RewardService(db, content.Rewards, ProgressionTestFactory.Create(), planting: content.Plants);
        foreach (var plant in content.Plants.Plants.Where(p => p.IsRare))
        {
            foreach (var depth in Enumerable.Range(1, 10))
            {
                var drop = Assert.Single(service.GetDropPreview(plant.UnlockTargetCode, true, depth), d => d.Code == plant.SeedCode);
                Assert.Equal(10 + (depth - 1) * 2, drop.ChancePercent);
                Assert.Equal(1, drop.Quantity);
            }
            Assert.Equal(100, Assert.Single(service.GetFirstSeedClearPreview(plant.UnlockTargetCode)).ChancePercent);
        }
    }

    private static StartProductionRequest Request(string recipeCode) => new()
    { CharacterId = 1, RecipeCode = recipeCode, RequestId = Guid.NewGuid().ToString("N"), TargetCycles = 1 };

    private static async Task<GameDbContext> CreateDbAsync()
    {
        var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        db.AddRange(new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 },
            new Character { Id = 1, UserId = 1, Name = "Farmer", Level = 1 },
            new UserLoginSession { UserId = 1, Token = "token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
        await db.SaveChangesAsync();
        return db;
    }

    private sealed class Content
    {
        private readonly IConfiguration _config = new ConfigurationBuilder().AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        public Content()
        {
            Weapons = new(Bind<WeaponOptions>(WeaponOptions.SectionName));
            Consumables = new(Bind<ConsumableOptions>(ConsumableOptions.SectionName));
            Materials = new(Bind<MaterialOptions>(MaterialOptions.SectionName));
            Plants = new(Bind<PlantingOptions>(PlantingOptions.SectionName));
            Production = new(Bind<ProductionOptions>(ProductionOptions.SectionName), World, Materials, Consumables);
            Shop = new(Bind<ShopOptions>(ShopOptions.SectionName), Consumables, Weapons, Plants, Materials);
            Souls = new(Bind<SoulImprintOptions>(SoulImprintOptions.SectionName));
            Exchanges = new(Bind<DungeonExchangeOptions>(DungeonExchangeOptions.SectionName), Materials, Weapons, Souls);
            Rewards = new(Bind<RewardOptions>(RewardOptions.SectionName), Consumables, Weapons, Materials, Souls);
        }
        private IOptions<T> Bind<T>(string section) where T : class, new() => Options.Create(_config.GetSection(section).Get<T>()!);
        public WorldCatalog World { get; } = WorldCatalog.LoadDefault();
        public WeaponCatalog Weapons { get; }
        public ConsumableCatalog Consumables { get; }
        public MaterialCatalog Materials { get; }
        public PlantingCatalog Plants { get; }
        public ProductionCatalog Production { get; }
        public ShopCatalog Shop { get; }
        public DungeonExchangeCatalog Exchanges { get; }
        public SoulImprintCatalog Souls { get; }
        public RewardCatalog Rewards { get; }
    }
}
