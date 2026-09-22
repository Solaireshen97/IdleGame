using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Dtos.Auth;
using Game.Shared.Dtos.Characters;
using Game.Shared.Dtos.Shop;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class T1WeaponEconomyTests
{
    [Fact]
    public async Task CharacterSlotsStartAtTwoAndCanBePurchasedUpToFive()
    {
        await using var test = await EconomyContext.CreateAsync();
        var initialAccount = await test.Users.GetCurrentUserAsync(test.Token);
        Assert.Null(initialAccount.Error);
        Assert.Equal(1, initialAccount.Response!.CharacterCount);
        Assert.Equal(2, initialAccount.Response.CharacterSlotLimit);
        Assert.Equal(5, initialAccount.Response.MaximumCharacterSlots);
        Assert.Equal(500, initialAccount.Response.NextCharacterSlotCost);

        Assert.Null((await test.Users.CreateCurrentCharacterAsync(test.Token,
            new CreateCharacterRequest { Name = "第二角色", ProfessionCode = "acolyte" })).Error);
        var blockedThird = await test.Users.CreateCurrentCharacterAsync(test.Token,
            new CreateCharacterRequest { Name = "第三角色", ProfessionCode = "swordsman" });
        Assert.Equal("CharacterSlotLimitReached", blockedThird.Error);
        Assert.Equal(2, await test.Db.Characters.CountAsync());

        test.User.Gold = 6000;
        await test.Db.SaveChangesAsync();
        var thirdSlot = await test.Shop.PurchaseCharacterSlotAsync(test.Token);
        Assert.Null(thirdSlot.Error);
        Assert.Equal(3, thirdSlot.Response!.CharacterSlotLimit);
        Assert.Equal(5500, thirdSlot.Response.Gold);
        Assert.Equal(1500, thirdSlot.Response.NextCharacterSlotCost);
        Assert.Null((await test.Users.CreateCurrentCharacterAsync(test.Token,
            new CreateCharacterRequest { Name = "第三角色", ProfessionCode = "swordsman" })).Error);

        Assert.Null((await test.Shop.PurchaseCharacterSlotAsync(test.Token)).Error);
        Assert.Null((await test.Users.CreateCurrentCharacterAsync(test.Token,
            new CreateCharacterRequest { Name = "第四角色", ProfessionCode = "acolyte" })).Error);
        var fifthSlot = await test.Shop.PurchaseCharacterSlotAsync(test.Token);
        Assert.Null(fifthSlot.Error);
        Assert.Equal(5, fifthSlot.Response!.CharacterSlotLimit);
        Assert.Null(fifthSlot.Response.NextCharacterSlotCost);
        Assert.Equal(0, fifthSlot.Response.Gold);
        Assert.Null((await test.Users.CreateCurrentCharacterAsync(test.Token,
            new CreateCharacterRequest { Name = "第五角色", ProfessionCode = "swordsman" })).Error);

        Assert.Equal("MaximumCharacterSlotsReached",
            (await test.Shop.PurchaseCharacterSlotAsync(test.Token)).Error);
        Assert.Equal("CharacterSlotLimitReached",
            (await test.Users.CreateCurrentCharacterAsync(test.Token,
                new CreateCharacterRequest { Name = "第六角色", ProfessionCode = "swordsman" })).Error);
        Assert.Equal(5, await test.Db.Characters.CountAsync());
    }

    [Fact]
    public async Task StartingGoldIsAccountOnlyAndStarterCannotBeSoldAfterUnlocking()
    {
        await using var test = await EconomyContext.CreateAsync();
        Assert.Equal(120, test.User.Gold);
        Assert.Equal(WeaponOrigin.Starter, (await test.Db.CharacterWeapons.SingleAsync()).Origin);
        var created = await test.Users.CreateCurrentCharacterAsync(test.Token,
            new CreateCharacterRequest { Name = "第二角色", ProfessionCode = "cleric" });
        Assert.Null(created.Error);
        Assert.Equal(120, test.User.Gold);

        // The backend still rejects the starter gift after it is unequipped and unlocked.
        var starter = await test.Db.CharacterWeapons.SingleAsync(weapon => weapon.CharacterId == test.Character.Id);
        starter.IsLocked = false;
        starter.EquippedSlotIndex = null;
        await test.Db.SaveChangesAsync();
        var sold = await test.Armory.SellAsync(test.Token, test.Character.Id,
            new WeaponBatchRequest { WeaponIds = [starter.Id] });
        Assert.Equal("StarterWeaponCannotBeSold", sold.Error);
        Assert.Equal(120, test.User.Gold);
        var recycled = await test.Armory.DismantleAsync(test.Token, test.Character.Id,
            new WeaponBatchRequest { WeaponIds = [starter.Id] });
        Assert.Equal("WeaponCannotBeDismantled", recycled.Error);
        Assert.Empty(await test.Db.CharacterItemStacks.ToListAsync());
        Assert.NotNull(await test.Db.CharacterWeapons.SingleOrDefaultAsync(item => item.Id == starter.Id));
    }

    [Fact]
    public async Task ShopWeaponsCannotCreateBaseFragmentsAndRefundOnlyActualEnhancementSpending()
    {
        await using var test = await EconomyContext.CreateAsync();
        var bought = await test.Shop.PurchaseAsync(test.Token,
            new PurchaseShopItemRequest { CharacterId = test.Character.Id, Code = "t1-shop-fire", Quantity = 1 });
        Assert.Null(bought.Error);
        Assert.Equal(80, test.User.Gold);
        var weapon = await test.Db.CharacterWeapons.Include(item => item.Skills).SingleAsync(item => item.Origin == WeaponOrigin.Shop);
        Assert.Equal(0, test.Weapons.DismantleReturn(weapon));
        Assert.False(test.Weapons.CanDismantle(weapon));
        Assert.Equal(0, weapon.Skills.Sum(skill => skill.QualityBonusLevel));
        var directRecycle = await test.Armory.DismantleAsync(test.Token, test.Character.Id,
            new WeaponBatchRequest { WeaponIds = [weapon.Id] });
        Assert.Equal("WeaponCannotBeDismantled", directRecycle.Error);
        Assert.NotNull(await test.Db.CharacterWeapons.SingleOrDefaultAsync(item => item.Id == weapon.Id));
        test.Db.CharacterItemStacks.Add(new CharacterItemStack { CharacterId = test.Character.Id, ItemCode = WeaponRules.FragmentCode(1), Quantity = 10 });
        await test.Db.SaveChangesAsync();

        Assert.Null((await test.Armory.EnhanceSkillAsync(test.Token, test.Character.Id, weapon.Id, 1)).Error);
        Assert.Null((await test.Armory.EnhanceSkillAsync(test.Token, test.Character.Id, weapon.Id, 1)).Error);
        Assert.Equal(10, weapon.Skills.Single().SpentFragments);
        Assert.Equal(5, test.Weapons.DismantleReturn(weapon));
        Assert.True(test.Weapons.CanDismantle(weapon));
        var rejected = await test.Armory.EnhanceSkillAsync(test.Token, test.Character.Id, weapon.Id, 1);
        Assert.Equal("InsufficientWeaponFragments", rejected.Error);
        Assert.Equal(2, weapon.Skills.Single().EnhancementLevel);
        var recycled = await test.Armory.DismantleAsync(test.Token, test.Character.Id,
            new WeaponBatchRequest { WeaponIds = [weapon.Id] });
        Assert.Null(recycled.Error);
        Assert.Equal(5, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Equal(80, test.User.Gold);
    }

    [Fact]
    public void PriceChangesDoNotRevalueHistoricMaterialsAndFullNewEnhancementCostsThirtyFour()
    {
        var catalog = T1WeaponEffectTests.ProductionCatalog();
        Assert.Equal(new[] { 2, 8, 24 }, Enumerable.Range(0, 3).Select(catalog.EnhancementCost));
        var old = new CharacterWeaponSkill { EnhancementLevel = 2, SpentFragments = 6 };
        Assert.Equal(6, catalog.InvestedFragments(old));
        old.SpentFragments += catalog.EnhancementCost(old.EnhancementLevel);
        old.EnhancementLevel++;
        Assert.Equal(30, catalog.InvestedFragments(old));
        Assert.Equal(14, catalog.InvestedFragments(new CharacterWeaponSkill { EnhancementLevel = 3 }));
        Assert.Equal(34, Enumerable.Range(0, 3).Sum(catalog.EnhancementCost));
    }

    [Fact]
    public async Task TutorialWeaponIsDeterministicCommonAndClaimedOnceAcrossCharactersAndRuns()
    {
        await using var test = await EconomyContext.CreateAsync();
        var dungeon = new Dungeon { Code = "northshire-wolves", Name = "新手讨伐", DungeonKind = "Hunt", MonsterName = "幼狼", MonsterMaxHp = 35 };
        var second = new Character { UserId = test.User.Id, Name = "替补", Hp = 40, MaxHp = 40, Attack = 16 };
        test.Db.AddRange(dungeon, second);
        await test.Db.SaveChangesAsync();
        var room = new Room { DungeonId = dungeon.Id, OwnerUserId = test.User.Id, RunSequence = 1 };
        test.Db.Rooms.Add(room);
        await test.Db.SaveChangesAsync();
        var rewards = new RewardService(test.Db, new RewardCatalog(test.Bind<RewardOptions>(RewardOptions.SectionName),
            test.Consumables, test.Weapons, test.Materials), ProgressionTestFactory.Create());

        // Two characters owned by the same account receive exactly one tutorial weapon in total.
        for (var run = 1; run <= 2; run++)
        {
            room.RunSequence = run;
            await rewards.RecordAsync(room, dungeon.Code,
                [new RewardParticipant(test.User.Id, test.Character), new RewardParticipant(test.User.Id, second)], "kill:1", false);
            await rewards.SettleAsync(room, true, DateTime.UtcNow, []);
            await test.Db.SaveChangesAsync();
            await rewards.SettleAsync(room, true, DateTime.UtcNow, []);
            await test.Db.SaveChangesAsync();
        }
        var tutorial = await test.Db.CharacterWeapons.Include(item => item.Skills).SingleAsync(item => item.Origin == WeaponOrigin.Tutorial);
        Assert.Equal("t1-fang-hunting-spear", tutorial.WeaponCode);
        Assert.Equal(test.Character.Id, tutorial.CharacterId);
        Assert.Equal(0, tutorial.Skills.Sum(skill => skill.QualityBonusLevel));
        Assert.True(test.User.StarterWeaponRewardClaimed);
        Assert.Single(await test.Db.RewardEntries.Where(entry => entry.EventKey == "starter-hunt-weapon").ToListAsync());
        Assert.Single(await test.Db.RewardEvents.Where(entry => entry.EventKey == "starter-hunt-weapon").ToListAsync());
    }

    private sealed class EconomyContext : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IConfiguration _configuration;
        public GameDbContext Db { get; }
        public UserService Users { get; }
        public WeaponCatalog Weapons { get; }
        public ConsumableCatalog Consumables { get; }
        public MaterialCatalog Materials { get; }
        public WeaponService Armory { get; }
        public ShopService Shop { get; }
        public User User { get; private set; } = null!;
        public Character Character { get; private set; } = null!;
        public string Token { get; private set; } = "";
        public IOptions<T> Bind<T>(string section) where T : class, new() => Options.Create(_configuration.GetSection(section).Get<T>()!);

        private EconomyContext(SqliteConnection connection)
        {
            _connection = connection;
            Db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
            _configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();
            Weapons = new WeaponCatalog(Bind<WeaponOptions>(WeaponOptions.SectionName));
            Consumables = new ConsumableCatalog(Bind<ConsumableOptions>(ConsumableOptions.SectionName));
            Materials = new MaterialCatalog(Bind<MaterialOptions>(MaterialOptions.SectionName));
            var skills = SkillTestFactory.Create();
            Users = new UserService(Db, ProgressionTestFactory.Create(), skills, Weapons);
            Armory = new WeaponService(Db, Users, skills, Weapons);
            Shop = new ShopService(Db, Users, new ShopCatalog(Bind<ShopOptions>(ShopOptions.SectionName), Consumables, Weapons),
                Consumables, Weapons, Materials, new DungeonExchangeCatalog(Bind<DungeonExchangeOptions>(DungeonExchangeOptions.SectionName), Materials, Weapons));
        }

        public static async Task<EconomyContext> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var test = new EconomyContext(connection);
            await test.Db.Database.EnsureCreatedAsync();
            var registered = await test.Users.RegisterAsync(new RegisterRequest { UserName = "economy", Password = "test-password" });
            Assert.Null(registered.Error);
            test.Token = registered.Response!.Token;
            test.User = await test.Db.Users.SingleAsync();
            test.Character = await test.Db.Characters.SingleAsync();
            return test;
        }

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }
}
