using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class WeaponBreakthroughTests
{
    [Fact]
    public void ProductionRecipeLoadsOnceWithT1BoundaryAndConversionCost()
    {
        var path = TestRepository.File("Game.Server", "appsettings.json");
        var options = new WeaponBreakthroughOptions();
        new ConfigurationBuilder().AddJsonFile(path).Build().GetSection(WeaponBreakthroughOptions.SectionName).Bind(options);
        var catalog = new WeaponBreakthroughCatalog(Options.Create(options));
        var recipe = Assert.Single(catalog.Recipes);
        Assert.Equal((1, 100, 1, 10), (recipe.Tier, recipe.FragmentsPerStone, recipe.MinimumWeaponItemLevel, recipe.MaximumWeaponItemLevel));
        Assert.Equal("weapon-breakthrough-fragment-t1", recipe.FragmentCode);
        Assert.Equal("weapon-breakthrough-stone-t1", recipe.StoneCode);
        Assert.Same(recipe, catalog.FindForWeapon(1));
        Assert.Same(recipe, catalog.FindForWeapon(10));
        Assert.Null(catalog.FindForWeapon(11));
    }

    [Fact]
    public async Task CraftConvertsExactlyConfiguredFragmentsAndNeverCreatesWeapons()
    {
        await using var test = await TestContext.CreateAsync();
        await test.AddStackAsync("weapon-breakthrough-fragment-t1", 250);
        await test.AddStackAsync("weapon-fragment-t1", 77);
        var (before, _) = await test.Service.GetAsync("token", 1);
        Assert.Equal(2, Assert.Single(before!.BreakthroughMaterials).CanCraftQuantity);

        var (response, error) = await test.Service.CraftBreakthroughStoneAsync("token", 1,
            new() { Tier = 1, Quantity = 2 });

        Assert.Null(error);
        var material = Assert.Single(response!.BreakthroughMaterials);
        Assert.Equal((50, 2, 0), (material.FragmentQuantity, material.StoneQuantity, material.CanCraftQuantity));
        Assert.Equal(100, material.FragmentsPerStone);
        Assert.Equal("T1 通用突破碎片", material.FragmentName);
        Assert.Equal(77, response.Fragments.Single(item => item.Tier == 1).Quantity);
        Assert.Single(response.Weapons);
    }

    [Fact]
    public async Task StoneReplacesOneWeaponAndOnlyRaisesQuality()
    {
        await using var test = await TestContext.CreateAsync();
        await test.AddStackAsync("weapon-breakthrough-stone-t1", 3);
        test.Target.IsLocked = true; // Existing breakthrough allows equipped and locked targets.
        await test.Db.SaveChangesAsync();
        var initialSkill = Assert.Single(test.Target.Skills);
        var initial = (test.Target.Attack, test.Target.MaxHp, initialSkill.Level, initialSkill.EnhancementLevel);
        for (var rank = 1; rank <= WeaponRules.MaxQualityBonusLevels; rank++)
        {
            var (response, error) = await test.Service.UpgradeQualityAsync("token", 1, test.Target.Id,
                new UpgradeWeaponQualityRequest { UseUniversalStone = true });
            Assert.Null(error);
            Assert.Equal(rank, test.Target.QualityRank);
            Assert.Equal(3 - rank, Assert.Single(response!.BreakthroughMaterials).StoneQuantity);
            Assert.Single(response.Weapons);
            Assert.Equal(initial, (test.Target.Attack, test.Target.MaxHp, initialSkill.Level, initialSkill.EnhancementLevel));
        }
        Assert.Equal("WeaponQualityAtMaximum", (await test.Service.UpgradeQualityAsync("token", 1,
            test.Target.Id, new UpgradeWeaponQualityRequest { UseUniversalStone = true })).Error);
    }

    [Fact]
    public async Task RejectsMissingTargetInsufficientStonesAndMixedMaterialSelectionWithoutConsumption()
    {
        await using var test = await TestContext.CreateAsync();
        Assert.Equal("InsufficientBreakthroughStones", (await test.Service.UpgradeQualityAsync("token", 1,
            test.Target.Id, new UpgradeWeaponQualityRequest { UseUniversalStone = true })).Error);
        await test.AddStackAsync("weapon-breakthrough-stone-t1", 1);
        Assert.Equal("WeaponNotOwned", (await test.Service.UpgradeQualityAsync("token", 1, 99999,
            new UpgradeWeaponQualityRequest { UseUniversalStone = true })).Error);
        Assert.Equal("InvalidQualityMaterial", (await test.Service.UpgradeQualityAsync("token", 1,
            test.Target.Id, new UpgradeWeaponQualityRequest { UseUniversalStone = true, MaterialWeaponId = test.Target.Id })).Error);
        Assert.Equal(0, test.Target.QualityRank);
        Assert.Equal(1, (await test.Service.GetAsync("token", 1)).Response!.BreakthroughMaterials.Single().StoneQuantity);
    }

    [Theory]
    [InlineData(0, 1, "InvalidBreakthroughCraftRequest")]
    [InlineData(1, 0, "InvalidBreakthroughCraftRequest")]
    [InlineData(1, -1, "InvalidBreakthroughCraftRequest")]
    [InlineData(1, int.MaxValue, "InvalidBreakthroughCraftRequest")]
    [InlineData(1, 3, "InsufficientBreakthroughFragments")]
    public async Task InvalidCraftPreservesInventory(int tier, int quantity, string expectedError)
    {
        await using var test = await TestContext.CreateAsync();
        await test.AddStackAsync("weapon-breakthrough-fragment-t1", 200);
        var result = await test.Service.CraftBreakthroughStoneAsync("token", 1, new() { Tier = tier, Quantity = quantity });
        Assert.Equal(expectedError, result.Error);
        var inventory = (await test.Service.GetAsync("token", 1)).Response!.BreakthroughMaterials.Single();
        Assert.Equal((200, 0), (inventory.FragmentQuantity, inventory.StoneQuantity));
    }

    [Fact]
    public async Task FullStoneStackRejectsCraftBeforeConsumingFragments()
    {
        await using var test = await TestContext.CreateAsync();
        await test.AddStackAsync("weapon-breakthrough-fragment-t1", 100);
        await test.AddStackAsync("weapon-breakthrough-stone-t1", int.MaxValue);
        Assert.Equal("InvalidBreakthroughCraftRequest", (await test.Service.CraftBreakthroughStoneAsync("token", 1, new())).Error);
        var inventory = (await test.Service.GetAsync("token", 1)).Response!.BreakthroughMaterials.Single();
        Assert.Equal((100, int.MaxValue, 0), (inventory.FragmentQuantity, inventory.StoneQuantity, inventory.CanCraftQuantity));
    }

    [Fact]
    public async Task FutureStageUsesIndependentConfiguredRecipeAndCannotConsumeT1Stone()
    {
        await using var test = await TestContext.CreateAsync(includeTier2: true);
        test.Target.ItemLevel = 11;
        await test.Db.SaveChangesAsync();
        await test.AddStackAsync("weapon-breakthrough-stone-t1", 1);
        Assert.Equal("InsufficientBreakthroughStones", (await test.Service.UpgradeQualityAsync("token", 1,
            test.Target.Id, new UpgradeWeaponQualityRequest { UseUniversalStone = true })).Error);
        await test.AddStackAsync("weapon-breakthrough-fragment-t2", 75);
        var crafted = await test.Service.CraftBreakthroughStoneAsync("token", 1, new() { Tier = 2 });
        Assert.Null(crafted.Error);
        Assert.Equal(25, crafted.Response!.BreakthroughMaterials.Single(item => item.Tier == 2).FragmentQuantity);
        var upgraded = await test.Service.UpgradeQualityAsync("token", 1, test.Target.Id,
            new UpgradeWeaponQualityRequest { UseUniversalStone = true });
        Assert.Null(upgraded.Error);
        Assert.Equal(2, Assert.Single(upgraded.Response!.Weapons).BreakthroughTier);
        Assert.Equal(1, upgraded.Response.BreakthroughMaterials.Single(item => item.Tier == 1).StoneQuantity);
        Assert.Equal(0, upgraded.Response.BreakthroughMaterials.Single(item => item.Tier == 2).StoneQuantity);
    }

    [Fact]
    public async Task UnsupportedStageNeverFallsBackToEarlierStone()
    {
        await using var test = await TestContext.CreateAsync();
        test.Target.ItemLevel = 11;
        await test.Db.SaveChangesAsync();
        await test.AddStackAsync("weapon-breakthrough-stone-t1", 1);
        Assert.Equal("WeaponBreakthroughStageUnsupported", (await test.Service.UpgradeQualityAsync("token", 1,
            test.Target.Id, new UpgradeWeaponQualityRequest { UseUniversalStone = true })).Error);
        var response = (await test.Service.GetAsync("token", 1)).Response!;
        Assert.Null(Assert.Single(response.Weapons).BreakthroughTier);
        Assert.Equal(1, response.BreakthroughMaterials.Single().StoneQuantity);
    }

    [Fact]
    public async Task BattleLockAndOwnershipProtectBothOperations()
    {
        await using var test = await TestContext.CreateAsync();
        await test.AddStackAsync("weapon-breakthrough-fragment-t1", 100);
        await test.AddStackAsync("weapon-breakthrough-stone-t1", 1);
        Assert.Equal("Unauthorized", (await test.Service.CraftBreakthroughStoneAsync(null, 1, new())).Error);
        Assert.Equal("NotOwner", (await test.Service.CraftBreakthroughStoneAsync("other-token", 1, new())).Error);
        Assert.Equal("NotOwner", (await test.Service.UpgradeQualityAsync("other-token", 1, test.Target.Id,
            new UpgradeWeaponQualityRequest { UseUniversalStone = true })).Error);
        test.Db.AddRange(new Dungeon { Id = 1, Code = "battle", Name = "Battle", MonsterName = "Monster" },
            new Monster { Id = 1, Name = "Monster", Hp = 100, MaxHp = 100 },
            new Room { Id = 1, DungeonId = 1, MonsterId = 1, OwnerUserId = 1, Status = RoomStatus.Preparing },
            new RoomSlot { RoomId = 1, SlotIndex = 1, CharacterId = 1, UserId = 1 });
        await test.Db.SaveChangesAsync();
        Assert.Equal("LoadoutLocked", (await test.Service.CraftBreakthroughStoneAsync("token", 1, new())).Error);
        Assert.Equal("LoadoutLocked", (await test.Service.UpgradeQualityAsync("token", 1, test.Target.Id,
            new UpgradeWeaponQualityRequest { UseUniversalStone = true })).Error);
        Assert.Equal(0, test.Target.QualityRank);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleConcurrentOperationRollsBackEntireInventoryAndUpgrade(bool craft)
    {
        await using var test = await TestContext.CreateAsync();
        await test.AddStackAsync("weapon-breakthrough-fragment-t1", 200);
        await test.AddStackAsync("weapon-breakthrough-stone-t1", 2);
        await using var staleDb = test.OpenDb();
        var staleService = test.CreateService(staleDb);
        // Retain the original concurrency versions as two overlapping requests would.
        await staleDb.Characters.LoadAsync();
        await staleDb.CharacterWeapons.Include(weapon => weapon.Skills).LoadAsync();
        await staleDb.CharacterItemStacks.LoadAsync();
        if (craft)
        {
            Assert.Null((await test.Service.CraftBreakthroughStoneAsync("token", 1, new())).Error);
            Assert.Equal("ConcurrencyConflict", (await staleService.CraftBreakthroughStoneAsync("token", 1, new())).Error);
        }
        else
        {
            var request = new UpgradeWeaponQualityRequest { UseUniversalStone = true };
            Assert.Null((await test.Service.UpgradeQualityAsync("token", 1, test.Target.Id, request)).Error);
            Assert.Equal("ConcurrencyConflict", (await staleService.UpgradeQualityAsync("token", 1, test.Target.Id, request)).Error);
        }
        await using var verification = test.OpenDb();
        Assert.Equal(craft ? 0 : 1, (await verification.CharacterWeapons.SingleAsync()).QualityRank);
        Assert.Equal(craft ? 100 : 200, (await verification.CharacterItemStacks.SingleAsync(item =>
            item.ItemCode == "weapon-breakthrough-fragment-t1")).Quantity);
        Assert.Equal(craft ? 3 : 1, (await verification.CharacterItemStacks.SingleAsync(item =>
            item.ItemCode == "weapon-breakthrough-stone-t1")).Quantity);
    }

    [Fact]
    public void CatalogRejectsOverlappingStageRangesAndSharedMaterialCodes()
    {
        var first = Recipe(1, 100);
        var second = Recipe(2, 50);
        second.MinimumWeaponItemLevel = 10;
        Assert.Throws<InvalidOperationException>(() => Catalog([first, second]));
        second.MinimumWeaponItemLevel = 11;
        second.StoneCode = first.StoneCode;
        Assert.Throws<InvalidOperationException>(() => Catalog([first, second]));
        first.FragmentsPerStone = 0;
        Assert.Throws<InvalidOperationException>(() => Catalog([first]));
    }

    private static WeaponBreakthroughRecipeOptions Recipe(int tier, int fragmentsPerStone) => new()
    {
        Tier = tier, FragmentCode = $"weapon-breakthrough-fragment-t{tier}", StoneCode = $"weapon-breakthrough-stone-t{tier}",
        FragmentsPerStone = fragmentsPerStone, MinimumWeaponItemLevel = (tier - 1) * 10 + 1, MaximumWeaponItemLevel = tier * 10
    };
    private static WeaponBreakthroughCatalog Catalog(List<WeaponBreakthroughRecipeOptions> recipes) =>
        new(Options.Create(new WeaponBreakthroughOptions { Recipes = recipes }));

    private sealed class TestContext : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"idlegame-breakthrough-{Guid.NewGuid():N}.db");
        private readonly WeaponCatalog _weapons = new(Options.Create(new WeaponOptions
        {
            Skills = [new() { Code = "weapon-attack", Name = "攻击", EffectType = WeaponSkillEffectType.AttackPercent, PercentPerLevel = 2 }],
            Items = [new() { Code = "test-fire", Name = "Test Fire", Element = ElementType.Fire, Attack = 20, MaxHp = 100,
                Skills = [new() { Code = "weapon-attack", Level = 1 }] }],
            StarterPacks = new Dictionary<string, List<string>> { ["knight"] = ["test-fire"] }
        }));
        private WeaponBreakthroughCatalog _breakthrough = null!;
        public GameDbContext Db { get; private set; } = null!;
        public CharacterWeapon Target { get; private set; } = null!;
        public WeaponService Service { get; private set; } = null!;
        public GameDbContext OpenDb() => new(new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite($"Data Source={_path};Pooling=False").Options);
        public WeaponService CreateService(GameDbContext db)
        {
            var skills = SkillTestFactory.Create();
            return new(db, new UserService(db, ProgressionTestFactory.Create(), skills), skills, _weapons, _breakthrough);
        }
        public static async Task<TestContext> CreateAsync(bool includeTier2 = false)
        {
            var test = new TestContext { _breakthrough = Catalog(includeTier2 ? [Recipe(1, 100), Recipe(2, 50)] : [Recipe(1, 100)]) };
            test.Db = test.OpenDb();
            await test.Db.Database.EnsureCreatedAsync();
            test.Target = test._weapons.CreateRewardSnapshot("test-fire").ToCharacterWeapon(1);
            test.Target.EquippedSlotIndex = 1;
            test.Db.AddRange(new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 },
                new User { Id = 2, UserName = "other", PasswordHash = "x" },
                new Character { Id = 1, UserId = 1, Name = "Knight", Hp = 100, MaxHp = 100, Attack = 20 },
                new UserLoginSession { UserId = 1, Token = "token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) },
                new UserLoginSession { UserId = 2, Token = "other-token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) },
                test.Target);
            await test.Db.SaveChangesAsync();
            test.Service = test.CreateService(test.Db);
            return test;
        }
        public async Task AddStackAsync(string code, int quantity)
        {
            Db.Add(new CharacterItemStack { CharacterId = 1, ItemCode = code, Quantity = quantity });
            await Db.SaveChangesAsync();
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            File.Delete(_path);
        }
    }
}
