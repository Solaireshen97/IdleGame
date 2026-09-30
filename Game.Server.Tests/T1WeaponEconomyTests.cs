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

        test.Character.Gold = 6000;
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

    [Theory]
    [InlineData("swordsman", "t1-shop-fire")]
    [InlineData("acolyte", "t1-shop-light")]
    [InlineData("mage", "t1-shop-water")]
    [InlineData("hunter", "t1-shop-wind")]
    [InlineData("rogue", "t1-shop-dark")]
    public async Task FirstCharacterUsesChosenProfessionAndReceivesStartingGoldAndShopWeapon(string professionCode, string weaponCode)
    {
        await using var test = await EconomyContext.CreateAsync(useProductionSkills: true, createFirstCharacter: false);
        Assert.Empty(await test.Db.Characters.ToListAsync());
        Assert.Empty(await test.Db.CharacterWeapons.ToListAsync());
        Assert.Empty(await test.Db.CharacterSkillSlots.ToListAsync());
        Assert.Null(test.User.ActiveCharacterId);
        var account = await test.Users.GetCurrentUserAsync(test.Token);
        Assert.Null(account.Error);
        Assert.Equal((0, 0), (account.Response!.CharacterCount, account.Response.Gold));

        var (created, error) = await test.Users.CreateCurrentCharacterAsync(test.Token,
            new CreateCharacterRequest { Name = "新角色", ProfessionCode = professionCode });

        Assert.Null(error);
        var character = await test.Db.Characters.SingleAsync(item => item.Id == created!.CharacterId);
        var weapons = await test.Db.CharacterWeapons.Include(item => item.Skills)
            .Where(item => item.CharacterId == character.Id).OrderBy(item => item.EquippedSlotIndex).ToListAsync();
        Assert.Equal(WeaponRules.SlotCount, weapons.Count);
        var weapon = weapons[0];
        Assert.All(weapons, item => Assert.Equal(weaponCode, item.WeaponCode));
        Assert.Equal(Enumerable.Range(1, WeaponRules.SlotCount), weapons.Select(item => item.EquippedSlotIndex!.Value));
        Assert.True(weapon.IsLocked);
        Assert.All(weapons.Skip(1), item => Assert.False(item.IsLocked));
        Assert.Equal(weaponCode, weapon.WeaponCode);
        Assert.Equal(WeaponRules.MainSlotIndex, weapon.EquippedSlotIndex);
        Assert.All(weapons, item => Assert.Equal("weapon-health-small", Assert.Single(item.Skills).SkillCode));
        Assert.Equal((900, 1100, 1320), (character.Attack, character.MaxHp, character.Hp));
        Assert.Equal(20m, character.WeaponHealthBonusPercent);
        var current = (await test.Users.GetCurrentCharacterAsync(test.Token)).Response!;
        Assert.Equal((900, 1320), (current.Attack, current.MaxHp));
        Assert.Equal(120, character.Gold);
        Assert.Equal(professionCode, character.ProfessionCode);
        Assert.Equal("新角色", character.Name);
        Assert.True(created!.IsCurrent);
        Assert.Equal(character.Id, test.User.ActiveCharacterId);
        Assert.Equal(character.Id, (await test.Users.GetCurrentCharacterAsync(test.Token)).Response!.CharacterId);
        var skills = new SkillCatalog(test.Bind<SkillOptions>(SkillOptions.SectionName));
        Assert.Equal(skills.FindProfession(professionCode)!.StartingSkills,
            await test.Db.CharacterSkillSlots.Where(slot => slot.CharacterId == character.Id).OrderBy(slot => slot.SlotIndex)
                .Select(slot => slot.SkillCode!).ToListAsync());

        var (second, secondError) = await test.Users.CreateCurrentCharacterAsync(test.Token,
            new CreateCharacterRequest { Name = "第二角色", ProfessionCode = professionCode });
        Assert.Null(secondError);
        Assert.False(second!.IsCurrent);
        Assert.Equal(0, (await test.Db.Characters.FindAsync(second.CharacterId))!.Gold);
        Assert.Equal(character.Id, test.User.ActiveCharacterId);
    }

    [Fact]
    public async Task RegistrationAndLoginDoNotCreateCharactersAndInvalidCreationDoesNotConsumeStartingGold()
    {
        await using var test = await EconomyContext.CreateAsync(useProductionSkills: true, createFirstCharacter: false);
        var (login, loginError) = await test.Users.LoginAsync(new LoginRequest { UserName = "economy", Password = "test-password" });
        Assert.Null(loginError);
        Assert.NotNull(login);
        Assert.Empty((await test.Users.GetCurrentCharactersAsync(login.Token)).Response!);
        Assert.Equal("CharacterNotFound", (await test.Users.GetCurrentCharacterAsync(login.Token)).Error);
        Assert.Equal("InvalidName", (await test.Users.CreateCurrentCharacterAsync(login.Token,
            new CreateCharacterRequest { Name = " ", ProfessionCode = "mage" })).Error);
        Assert.Empty(await test.Db.Characters.ToListAsync());
        Assert.Null(test.User.ActiveCharacterId);

        var (first, error) = await test.Users.CreateCurrentCharacterAsync(login.Token,
            new CreateCharacterRequest { Name = "我的法师", ProfessionCode = "mage" });
        Assert.Null(error);
        Assert.Equal(120, (await test.Db.Characters.FindAsync(first!.CharacterId))!.Gold);
        Assert.Equal(test.User.ActiveCharacterId, first.CharacterId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartingGoldBelongsOnlyToFirstCharacterAndStarterCanBeSoldAfterReplacementAndUnlocking(bool batch)
    {
        await using var test = await EconomyContext.CreateAsync();
        Assert.Equal(120, test.Character.Gold);
        Assert.All(await test.Db.CharacterWeapons.ToListAsync(), item => Assert.Equal(WeaponOrigin.Starter, item.Origin));
        var created = await test.Users.CreateCurrentCharacterAsync(test.Token,
            new CreateCharacterRequest { Name = "第二角色", ProfessionCode = "cleric" });
        Assert.Null(created.Error);
        Assert.Equal(120, test.Character.Gold);
        Assert.Equal(0, (await test.Db.Characters.SingleAsync(item => item.Id == created.Response!.CharacterId)).Gold);

        var starter = await test.Db.CharacterWeapons.SingleAsync(weapon =>
            weapon.CharacterId == test.Character.Id && weapon.EquippedSlotIndex == WeaponRules.MainSlotIndex);
        var inventory = (await test.Armory.GetAsync(test.Token, test.Character.Id)).Response!;
        Assert.Equal(WeaponRules.SlotCount, inventory.Weapons.Count);
        var initialWeapon = Assert.Single(inventory.Weapons, item => item.Id == starter.Id);
        Assert.True(initialWeapon.CanSell);
        Assert.Equal(starter.SellGold, initialWeapon.SellGold);
        Assert.Equal("WeaponEquipped", (await test.Armory.SellAsync(test.Token, test.Character.Id,
            new WeaponBatchRequest { WeaponIds = [starter.Id] })).Error);

        for (var index = 0; index < (batch ? 2 : 1); index++)
        {
            var bought = await test.Shop.PurchaseAsync(test.Token,
                new PurchaseShopItemRequest { CharacterId = test.Character.Id, Code = "t1-shop-fire", Quantity = 1, RequestId = Guid.NewGuid().ToString("N") });
            Assert.Null(bought.Error);
        }
        var replacements = await test.Db.CharacterWeapons.Where(weapon =>
            weapon.CharacterId == test.Character.Id && weapon.Origin == WeaponOrigin.Shop).OrderBy(weapon => weapon.Id).ToListAsync();
        var replacement = replacements[0];
        Assert.Null((await test.Armory.SetSlotAsync(test.Token, test.Character.Id, WeaponRules.MainSlotIndex,
            new SetWeaponSlotRequest { WeaponId = replacement.Id })).Error);
        Assert.Equal("WeaponLocked", (await test.Armory.SellAsync(test.Token, test.Character.Id,
            new WeaponBatchRequest { WeaponIds = [starter.Id] })).Error);
        Assert.Null((await test.Armory.SetLockAsync(test.Token, test.Character.Id, starter.Id,
            new SetWeaponLockRequest { IsLocked = false })).Error);

        var recycled = await test.Armory.DismantleAsync(test.Token, test.Character.Id,
            new WeaponBatchRequest { WeaponIds = [starter.Id] });
        Assert.Equal("WeaponCannotBeDismantled", recycled.Error);
        Assert.Empty(await test.Db.CharacterItemStacks.ToListAsync());
        Assert.NotNull(await test.Db.CharacterWeapons.SingleOrDefaultAsync(item => item.Id == starter.Id));

        var selected = new List<CharacterWeapon> { starter };
        if (batch) selected.Add(replacements[1]);
        var expectedGold = test.Character.Gold + selected.Sum(weapon => weapon.SellGold);
        var sold = await test.Armory.SellAsync(test.Token, test.Character.Id,
            new WeaponBatchRequest { WeaponIds = selected.Select(weapon => weapon.Id).ToList() });

        Assert.Null(sold.Error);
        Assert.Equal(expectedGold, sold.Response!.Gold);
        Assert.Equal(expectedGold, (await test.Db.Characters.AsNoTracking().SingleAsync(item => item.Id == test.Character.Id)).Gold);
        Assert.DoesNotContain(sold.Response.Weapons, weapon => selected.Any(item => item.Id == weapon.Id));
        Assert.False(await test.Db.CharacterWeapons.AnyAsync(weapon => selected.Select(item => item.Id).Contains(weapon.Id)));
        Assert.Equal(WeaponRules.SlotCount, sold.Response.Weapons.Count);
        Assert.Equal(replacement.Id, Assert.Single(sold.Response.Weapons,
            item => item.EquippedSlotIndex == WeaponRules.MainSlotIndex).Id);
    }

    [Fact]
    public async Task ShopWeaponsCannotCreateBaseFragmentsAndRefundOnlyActualEnhancementSpending()
    {
        await using var test = await EconomyContext.CreateAsync();
        var bought = await test.Shop.PurchaseAsync(test.Token,
            new PurchaseShopItemRequest { CharacterId = test.Character.Id, Code = "t1-shop-fire", Quantity = 1, RequestId = Guid.NewGuid().ToString("N") });
        Assert.Null(bought.Error);
        Assert.Equal(80, test.Character.Gold);
        var weapon = await test.Db.CharacterWeapons.Include(item => item.Skills).SingleAsync(item => item.Origin == WeaponOrigin.Shop);
        Assert.Equal(0, weapon.QualityRank);
        Assert.Equal(0, test.Weapons.DismantleReturn(weapon));
        Assert.False(test.Weapons.CanDismantle(weapon));
        Assert.Equal(0, weapon.Skills.Sum(skill => skill.QualityBonusLevel));
        var directRecycle = await test.Armory.DismantleAsync(test.Token, test.Character.Id,
            new WeaponBatchRequest { WeaponIds = [weapon.Id] });
        Assert.Equal("WeaponCannotBeDismantled", directRecycle.Error);
        Assert.NotNull(await test.Db.CharacterWeapons.SingleOrDefaultAsync(item => item.Id == weapon.Id));
        test.Db.CharacterItemStacks.Add(new CharacterItemStack { CharacterId = test.Character.Id, ItemCode = WeaponRules.FragmentCode(1), Quantity = 10 });
        await test.Db.SaveChangesAsync();

        Assert.Null((await test.Armory.EnhanceSkillAsync(test.Token, test.Character.Id, weapon.Id, 1, Guid.NewGuid().ToString("N"))).Error);
        Assert.Null((await test.Armory.EnhanceSkillAsync(test.Token, test.Character.Id, weapon.Id, 1, Guid.NewGuid().ToString("N"))).Error);
        Assert.Equal(6, weapon.Skills.Single().SpentFragments);
        Assert.Equal(3, test.Weapons.DismantleReturn(weapon));
        Assert.True(test.Weapons.CanDismantle(weapon));
        var rejected = await test.Armory.EnhanceSkillAsync(test.Token, test.Character.Id, weapon.Id, 1, Guid.NewGuid().ToString("N"));
        Assert.Equal("InsufficientWeaponFragments", rejected.Error);
        Assert.Equal(2, weapon.Skills.Single().EnhancementLevel);
        var recycled = await test.Armory.DismantleAsync(test.Token, test.Character.Id,
            new WeaponBatchRequest { WeaponIds = [weapon.Id] });
        Assert.Null(recycled.Error);
        Assert.Equal(7, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Equal(80, test.Character.Gold);
    }

    [Fact]
    public async Task BoughtSameTemplateCanReplaceStarterOffhandWithoutChangingStartingStats()
    {
        await using var test = await EconomyContext.CreateAsync();
        var main = await test.Db.CharacterWeapons.SingleAsync(item =>
            item.CharacterId == test.Character.Id && item.EquippedSlotIndex == WeaponRules.MainSlotIndex);
        var offhand = await test.Db.CharacterWeapons.SingleAsync(item =>
            item.CharacterId == test.Character.Id && item.EquippedSlotIndex == 2);
        var purchase = await test.Shop.PurchaseAsync(test.Token,
            new PurchaseShopItemRequest { CharacterId = test.Character.Id, Code = main.WeaponCode,
                Quantity = 1, RequestId = Guid.NewGuid().ToString("N") });
        Assert.Null(purchase.Error);
        var bought = await test.Db.CharacterWeapons.SingleAsync(item => item.CharacterId == test.Character.Id &&
            item.Origin == WeaponOrigin.Shop);
        Assert.Null(bought.EquippedSlotIndex);

        var equipped = await test.Armory.SetSlotAsync(test.Token, test.Character.Id, 2,
            new SetWeaponSlotRequest { WeaponId = bought.Id });
        Assert.Null(equipped.Error);
        Assert.Null(offhand.EquippedSlotIndex);
        Assert.Equal(2, bought.EquippedSlotIndex);
        Assert.True(main.IsLocked);
        Assert.Equal((900, 1100, 1320), (test.Character.Attack, test.Character.MaxHp, test.Character.Hp));
        Assert.Equal(20m, test.Character.WeaponHealthBonusPercent);
    }

    [Fact]
    public void NineEnhancementStepsCostSixtyTwoAndRecordedOldInvestmentIsNotRevalued()
    {
        var catalog = T1WeaponEffectTests.ProductionCatalog();
        Assert.Equal(new[] { 2, 4, 8, 8, 8, 8, 8, 8, 8 }, Enumerable.Range(0, 9).Select(catalog.EnhancementCost));
        var old = new CharacterWeaponSkill { EnhancementLevel = 2, SpentFragments = 6 };
        Assert.Equal(6, catalog.InvestedFragments(old));
        old.SpentFragments += catalog.EnhancementCost(old.EnhancementLevel);
        old.EnhancementLevel++;
        Assert.Equal(14, catalog.InvestedFragments(old));
        Assert.Equal(14, catalog.InvestedFragments(new CharacterWeaponSkill { EnhancementLevel = 3 }));
        Assert.Equal(new[] { 0, 2, 6, 14, 30, 62, 126, 134, 142, 150 },
            Enumerable.Range(0, 10).Select(level =>
                catalog.InvestedFragments(new CharacterWeaponSkill { EnhancementLevel = level })));
        Assert.Equal(62, Enumerable.Range(0, 9).Sum(catalog.EnhancementCost));
    }

    [Fact]
    public async Task FirstHuntWeaponsAreClaimedPerCharacterAndRegionWithoutDuplicateSettlement()
    {
        await using var test = await EconomyContext.CreateAsync();
        var dungeon = new Dungeon { Code = "northshire-wolves", Name = "新手讨伐", DungeonKind = "Hunt", MonsterName = "幼狼", MonsterMaxHp = 35 };
        var water = new Dungeon { Code = "dun-morogh-snow-hare", Name = "雪境讨伐", DungeonKind = "Hunt", MonsterName = "躁兔", MonsterMaxHp = 35 };
        var later = new Dungeon { Code = "durotar-red-scorpion", Name = "其他讨伐", DungeonKind = "Hunt", MonsterName = "烬尾蝎", MonsterMaxHp = 35 };
        var second = new Character { UserId = test.User.Id, Name = "替补", Hp = 40, MaxHp = 40, Attack = 16 };
        test.Db.AddRange(dungeon, water, later, second);
        await test.Db.SaveChangesAsync();
        var room = new Room { DungeonId = dungeon.Id, OwnerUserId = test.User.Id, RunSequence = 1 };
        test.Db.Rooms.Add(room);
        await test.Db.SaveChangesAsync();
        var rewards = new RewardService(test.Db, new RewardCatalog(test.Bind<RewardOptions>(RewardOptions.SectionName),
            test.Consumables, test.Weapons, test.Materials,
            new SoulImprintCatalog(test.Bind<SoulImprintOptions>(SoulImprintOptions.SectionName))),
            ProgressionTestFactory.Create());

        async Task Complete(int sequence, Dungeon target, params Character[] participants)
        {
            room.RunSequence = sequence;
            room.DungeonId = target.Id;
            await rewards.RecordAsync(room, target.Code,
                participants.Select(character => new RewardParticipant(test.User.Id, character)), "kill:1", false);
            await rewards.SettleAsync(room, true, DateTime.UtcNow, []);
            await test.Db.SaveChangesAsync();
            await rewards.SettleAsync(room, true, DateTime.UtcNow, []);
            await test.Db.SaveChangesAsync();
        }
        await Complete(1, dungeon, test.Character, second);
        await Complete(2, dungeon, test.Character, second);
        await Complete(3, water, test.Character);
        await Complete(4, later, second);

        var tutorials = await test.Db.CharacterWeapons.Include(item => item.Skills)
            .Where(item => item.Origin == WeaponOrigin.Tutorial).ToListAsync();
        Assert.Equal(3, tutorials.Count);
        Assert.Equal(2, tutorials.Count(item => item.WeaponCode == "t1-stone-edge-hatchet"));
        Assert.Single(tutorials, item => item.WeaponCode == "t1-ice-tusk-mallet" &&
            item.CharacterId == test.Character.Id);
        Assert.All(tutorials, item =>
        {
            Assert.Equal(0, item.QualityRank);
            Assert.Single(item.Skills);
            Assert.Equal(1, item.Skills.Single().Level);
            Assert.Equal(test.Weapons.FindItem(item.WeaponCode)!.Skills
                .Where(skill => skill.UnlockQualityRank == 0).Select(skill => skill.Code),
                item.Skills.OrderBy(skill => skill.SlotIndex).Select(skill => skill.SkillCode));
        });
        Assert.Equal(3, await test.Db.CharacterFirstHuntWeaponClaims.CountAsync());
        Assert.Equal(3, await test.Db.RewardEntries.CountAsync(entry => entry.EventKey == "starter-hunt-weapon"));
        Assert.Equal(2, await test.Db.RewardEvents.CountAsync(entry => entry.EventKey == "starter-hunt-weapon"));
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

        private EconomyContext(SqliteConnection connection, bool useProductionSkills)
        {
            _connection = connection;
            Db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
            _configuration = new ConfigurationBuilder().AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
            Weapons = new WeaponCatalog(Bind<WeaponOptions>(WeaponOptions.SectionName));
            Consumables = new ConsumableCatalog(Bind<ConsumableOptions>(ConsumableOptions.SectionName));
            Materials = new MaterialCatalog(Bind<MaterialOptions>(MaterialOptions.SectionName));
            var soulImprints = new SoulImprintCatalog(Bind<SoulImprintOptions>(SoulImprintOptions.SectionName));
            var skills = useProductionSkills
                ? new SkillCatalog(Bind<SkillOptions>(SkillOptions.SectionName))
                : SkillTestFactory.Create();
            Users = new UserService(Db, ProgressionTestFactory.Create(), skills, Weapons);
            Armory = new WeaponService(Db, Users, skills, Weapons);
            var plants = new PlantingCatalog(Bind<PlantingOptions>(PlantingOptions.SectionName));
            Shop = new ShopService(Db, Users, new ShopCatalog(Bind<ShopOptions>(ShopOptions.SectionName), Consumables, Weapons, plants, Materials),
                Consumables, Weapons, Materials, new DungeonExchangeCatalog(
                    Bind<DungeonExchangeOptions>(DungeonExchangeOptions.SectionName), Materials, Weapons, soulImprints), soulImprints);
        }

        public static async Task<EconomyContext> CreateAsync(bool useProductionSkills = false, bool createFirstCharacter = true)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var test = new EconomyContext(connection, useProductionSkills);
            await test.Db.Database.EnsureCreatedAsync();
            var registered = await test.Users.RegisterAsync(new RegisterRequest { UserName = "economy", Password = "test-password" });
            Assert.Null(registered.Error);
            test.Token = registered.Response!.Token;
            test.User = await test.Db.Users.SingleAsync();
            if (createFirstCharacter)
            {
                var created = await test.Users.CreateCurrentCharacterAsync(test.Token,
                    new CreateCharacterRequest { Name = "剑士", ProfessionCode = SkillRules.DefaultProfessionCode });
                Assert.Null(created.Error);
                test.Character = await test.Db.Characters.SingleAsync();
            }
            return test;
        }

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }
}
