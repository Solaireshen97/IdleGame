using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Dtos.Shop;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class WorldContentTests
{
    [Fact]
    public void ProductionRareGatheringPointsRequireEliteVictoriesAndHaveWarehouseMaterials()
    {
        var content = new Content();
        var gathering = new GatheringCatalog(content.Bind<GatheringOptions>(GatheringOptions.SectionName),
            content.World, content.Materials);
        var rarePoints = gathering.Points.Where(point => point.IsRare).ToList();
        Assert.Equal(2, rarePoints.Count);
        Assert.All(rarePoints, point =>
        {
            Assert.Equal("Elite", content.World.Dungeons.Single(dungeon =>
                dungeon.Code == point.UnlockTargetCode).DungeonKind);
            Assert.True(content.Materials.FindItem(point.MaterialCode)!.CanStoreInWarehouse);
            Assert.Equal(20, point.CycleSeconds);
        });
    }

    [Fact]
    public void ProductionWorldHasSixCompleteRegionsWithExclusiveBossLootAndIndependentExchanges()
    {
        var content = new Content();
        content.World.ValidateContent(content.Weapons, content.Encounters, content.Rewards, content.Exchanges);
        Assert.Equal(6, content.World.Regions.Count);
        Assert.Equal(6, content.World.Regions.Select(region => region.FeaturedElement).Distinct().Count());
        Assert.Equal(7, content.Exchanges.Offers.Select(offer => offer.CurrencyCode).Distinct().Count());
        Assert.Equal(42, content.Exchanges.Offers.Select(offer => offer.WeaponCode).Distinct().Count());
        Assert.Equal(42, content.Exchanges.Offers.Select(offer => content.Weapons.FindItem(offer.WeaponCode)!.Name).Distinct().Count());

        foreach (var region in content.World.Regions)
        {
            var dungeons = content.World.Dungeons.Where(dungeon => dungeon.RegionCode == region.Code).ToList();
            Assert.Equal((1, 10), (region.MinimumLevel, region.MaximumLevel));
            Assert.Equal(Enumerable.Range(1, 10), dungeons.Select(dungeon => dungeon.MinimumLevel).Distinct().Order());
            Assert.Equal(region.Code == "elwynn" ? 11 : 10, dungeons.Count);
            Assert.Equal(7, dungeons.Count(dungeon => dungeon.DungeonKind == "Hunt"));
            Assert.Equal(2, dungeons.Count(dungeon => dungeon.DungeonKind == "Elite"));
            Assert.All(dungeons, dungeon => Assert.True(dungeon.IsVisible));
            var dungeon = Assert.Single(dungeons, dungeon => dungeon.Code == region.FeaturedDungeonCode);
            Assert.Equal((8, 10), (dungeon.MinimumLevel, dungeon.RecommendedLevel));
            Assert.Equal(region.FeaturedDungeonCode, dungeon.Code);
            Assert.Equal(4, content.Encounters.GetWaveCount(dungeon));
            var monsters = content.Encounters.CreateMonsters(dungeon);
            Assert.Equal(7, monsters.Count);
            var boss = Assert.Single(monsters, monster => monster.IsBoss);
            Assert.Same(monsters.Last(), boss);
            Assert.Equal(region.FeaturedElement, boss.Element);
            Assert.Equal(3, content.Combat.FindProfile(boss.CombatProfileCode)!.Skills.Count);

            var bossWeapon = content.Weapons.FindItem(region.FeaturedWeaponCode)!;
            Assert.Equal(region.FeaturedElement, bossWeapon.Element);
            var bossDrop = Assert.Single(content.RewardOptions.MonsterKills[boss.RewardProfileCode].Drops,
                drop => drop.Code == region.FeaturedWeaponCode);
            Assert.Equal(18m, bossDrop.ChancePercent);
            Assert.Single(content.RewardOptions.MonsterKills, pair => pair.Value.Drops.Any(drop => drop.Code == region.FeaturedWeaponCode));
            Assert.DoesNotContain(content.RewardOptions.DungeonClears.Values, bundle => bundle.Drops.Any(drop => drop.Code == region.FeaturedWeaponCode));

            var offers = content.Exchanges.Offers.Where(offer => offer.DungeonCode == dungeon.Code).ToList();
            Assert.Equal(6, offers.Count);
            Assert.Equal(6, offers.Select(offer => content.Weapons.FindItem(offer.WeaponCode)!.Element).Distinct().Count());
            Assert.All(offers, offer => Assert.Equal(12, offer.Cost));
            Assert.DoesNotContain(offers, offer => offer.WeaponCode == region.FeaturedWeaponCode);
            var token = Assert.Single(offers.Select(offer => offer.CurrencyCode).Distinct());
            var repeatDrop = Assert.Single(content.RewardOptions.DungeonClears[dungeon.Code].Drops, drop => drop.Kind == "Material");
            var firstDrop = Assert.Single(content.RewardOptions.DungeonClears[$"{dungeon.Code}-first-clear"].Drops, drop => drop.Kind == "Material");
            Assert.Equal((token, 1, 100m), (repeatDrop.Code, repeatDrop.Quantity, repeatDrop.ChancePercent));
            Assert.Equal((token, 2, 100m), (firstDrop.Code, firstDrop.Quantity, firstDrop.ChancePercent));
        }
    }

    [Theory]
    [InlineData("kobold-mine")]
    [InlineData("spider-canyon")]
    [InlineData("ragefire-chasm")]
    [InlineData("frostspring-cavern")]
    [InlineData("windfury-nest")]
    [InlineData("dawn-ruins")]
    [InlineData("kobold-mine-depths")]
    public async Task RegionDungeonEnforcesLevelAndSettlesItsOwnTokensAcrossRuns(string dungeonCode)
    {
        var content = new Content();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        await DbInitializer.EnsureDefaultDungeonsAsync(db, content.World);
        var character = new Character { Id = 1, UserId = 1, Name = "主控", Level = 7, Hp = 100, MaxHp = 100, Attack = 20};
        var alternate = new Character { Id = 2, UserId = 1, Name = "替补", Level = 7, Hp = 100, MaxHp = 100, Attack = 20};
        var guest = new Character { Id = 3, UserId = 2, Name = "访客", Level = 7, Hp = 100, MaxHp = 100, Attack = 20};
        db.AddRange(character, alternate, guest,
            new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 },
            new User { Id = 2, UserName = "guest", PasswordHash = "x", ActiveCharacterId = 3 },
            new UserLoginSession { UserId = 1, Token = "owner-token", ExpireAt = DateTime.UtcNow.AddDays(1) },
            new UserLoginSession { UserId = 2, Token = "guest-token", ExpireAt = DateTime.UtcNow.AddDays(1) });
        await db.SaveChangesAsync();
        var progression = new ProgressionService(content.Bind<ProgressionOptions>(ProgressionOptions.SectionName));
        var skills = new SkillCatalog(content.Bind<SkillOptions>(SkillOptions.SectionName));
        var users = new UserService(db, progression, skills);
        var rewards = new RewardService(db, content.Rewards, progression);
        var rooms = new RoomService(db, users, progression, content.Consumables, skills, rewards,
            content.Encounters, worldCatalog: content.World);
        var dungeon = await db.Dungeons.SingleAsync(dungeon => dungeon.Code == dungeonCode);
        var rejected = await rooms.CreateRoomAsync(dungeon.Id, null, "owner-token");
        Assert.Equal("CharacterLevelTooLow", rejected.Error);
        Assert.Empty(await db.Rooms.ToListAsync());
        character.Level = dungeon.MinimumLevel;
        await db.SaveChangesAsync();
        var created = await rooms.CreateRoomAsync(dungeon.Id, null, "owner-token", isPreparationTimeoutEnabled: false);
        Assert.Null(created.Error);
        Assert.Equal(dungeon.RegionName, created.Detail!.RegionName);
        Assert.Equal(dungeon.RegionCode, Assert.Single(await rooms.GetRoomsAsync()).RegionCode);
        var join = await rooms.JoinRoomAsync(created.Detail.RoomId, new JoinRoomRequest { SlotIndex = 2 }, "guest-token");
        Assert.Equal("CharacterLevelTooLow", join.Error);
        var assign = await rooms.AssignSlotAsync(created.Detail.RoomId,
            new AssignRoomSlotRequest { CharacterId = alternate.Id, SlotIndex = 2 }, "owner-token");
        Assert.Equal("CharacterLevelTooLow", assign.Error);

        var room = await db.Rooms.SingleAsync();
        var runService = new DungeonRunService(db, rewards);
        var monsters = await db.Monsters.Where(monster => monster.RoomId == room.Id)
            .OrderBy(monster => monster.WaveNumber).ThenBy(monster => monster.Position).ToListAsync();
        var offer = content.Exchanges.Offers.First(offer => offer.DungeonCode == dungeonCode);
        for (var run = 1; run <= 2; run++)
        {
            if (run > 1) { room.RunSequence++; await runService.ResetEncounterAsync(room); }
            foreach (var monster in monsters)
            {
                monster.Hp = 0;
                var advanced = await runService.AdvanceAfterDefeatAsync(room, monster,
                    [new RewardParticipant(1, character)], DateTime.UtcNow, []);
                Assert.Null(advanced.Error);
                Assert.Equal(monster == monsters.Last(), advanced.IsDungeonComplete);
                await db.SaveChangesAsync();
                if (run == 1 && !advanced.IsDungeonComplete)
                    Assert.False(await db.CharacterItemStacks.AnyAsync(stack => stack.ItemCode == offer.CurrencyCode));
            }
            var token = await db.CharacterItemStacks.SingleAsync(stack => stack.ItemCode == offer.CurrencyCode);
            Assert.Equal(run == 1 ? 3 : 4, token.Quantity);
            Assert.Equal(character.Id, token.CharacterId);
            // Re-settling an already settled run must never credit a second time.
            await rewards.SettleAsync(room, true, DateTime.UtcNow, []);
            await db.SaveChangesAsync();
            Assert.Equal(run == 1 ? 3 : 4, token.Quantity);
        }
        Assert.Single(await db.UserDungeonClears.ToListAsync());
        Assert.True((await db.Users.FindAsync(1))!.Gold > 0);
        Assert.All(await db.CharacterItemStacks.ToListAsync(), stack => Assert.Equal(character.Id, stack.CharacterId));

        var otherToken = content.Exchanges.Offers.First(candidate => candidate.CurrencyCode != offer.CurrencyCode).CurrencyCode;
        db.CharacterItemStacks.Add(new CharacterItemStack { CharacterId = character.Id, ItemCode = otherToken, Quantity = 99 });
        await db.SaveChangesAsync();
        var shop = new ShopService(db, users,
            new ShopCatalog(content.Bind<ShopOptions>(ShopOptions.SectionName), content.Consumables, content.Weapons),
            content.Consumables, content.Weapons, content.Materials, content.Exchanges);
        var request = new ExchangeDungeonWeaponRequest { CharacterId = character.Id, OfferCode = offer.Code };
        var insufficient = await shop.ExchangeAsync("owner-token", request);
        Assert.Equal("InsufficientDungeonCurrency", insufficient.Error);
        var correctToken = await db.CharacterItemStacks.SingleAsync(stack => stack.ItemCode == offer.CurrencyCode);
        correctToken.Quantity = offer.Cost;
        await db.SaveChangesAsync();
        var exchanged = await shop.ExchangeAsync("owner-token", request);
        Assert.Null(exchanged.Error);
        Assert.Equal(0, correctToken.Quantity);
        Assert.Equal(99, (await db.CharacterItemStacks.SingleAsync(stack => stack.ItemCode == otherToken)).Quantity);
        Assert.Contains(await db.CharacterWeapons.ToListAsync(), weapon => weapon.WeaponCode == offer.WeaponCode && weapon.CharacterId == character.Id);
    }

    [Fact]
    public async Task MigrationAndRepeatedSeedingPreserveExistingRoomsAndClears()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.GetService<IMigrator>().MigrateAsync("20260921100000_AddDungeonContentProgression");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Dungeons (Id,Code,Name,MonsterName,MonsterMaxHp,MonsterAttack,MonsterDefense,MonsterElement,SlotCount,SortOrder,RegionName,DungeonKind,Description,MinimumLevel,RecommendedLevel,IsVisible) VALUES (55,'kobold-mine','狗头人矿洞','金牙',140,15,5,'Earth',5,8,'艾尔文森林','Dungeon','旧描述',8,8,1)");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Monsters (Id,Name,Element,Hp,MaxHp,Attack,Defense,RoomId,WaveNumber,Position,CombatProfileCode,RewardProfileCode,IsBoss) VALUES (7,'金牙','Earth',41,140,15,5,9,4,1,'','',1)");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Rooms (Id,DungeonId,MonsterId,OwnerUserId,SlotCount,Status,IsPreparationTimeoutEnabled,IsRepeatBattle,RoundNumber,RunSequence,Version,CurrentWaveNumber,TotalWaveCount) VALUES (9,55,7,1,5,0,1,0,0,1,0,4,4)");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO UserDungeonClears (UserId,DungeonId,ClearedAtUtc) VALUES (1,55,'2026-09-21 00:00:00')");
        var world = WorldCatalog.LoadDefault();
        await DbInitializer.InitializeAsync(db, world: world);
        await DbInitializer.InitializeAsync(db, world: world);
        Assert.Equal(64, await db.Dungeons.CountAsync());
        Assert.Equal(55, (await db.Dungeons.SingleAsync(dungeon => dungeon.Code == "kobold-mine")).Id);
        Assert.Equal("elwynn", (await db.Dungeons.FindAsync(55))!.RegionCode);
        Assert.Equal(55, (await db.Rooms.SingleAsync()).DungeonId);
        Assert.Equal(41, (await db.Monsters.SingleAsync()).Hp);
        Assert.Equal(55, (await db.UserDungeonClears.SingleAsync()).DungeonId);
        Assert.All(world.Dungeons, dungeon => Assert.Equal(0, dungeon.Id));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private sealed class Content
    {
        private readonly IConfiguration _configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();
        public IOptions<T> Bind<T>(string section) where T : class, new() => Options.Create(_configuration.GetSection(section).Get<T>()!);
        public WorldCatalog World { get; } = WorldCatalog.LoadDefault();
        public ConsumableCatalog Consumables { get; }
        public WeaponCatalog Weapons { get; }
        public MaterialCatalog Materials { get; }
        public RewardOptions RewardOptions { get; }
        public RewardCatalog Rewards { get; }
        public MonsterCombatCatalog Combat { get; }
        public DungeonEncounterCatalog Encounters { get; }
        public DungeonExchangeCatalog Exchanges { get; }

        public Content()
        {
            Consumables = new ConsumableCatalog(Bind<ConsumableOptions>(ConsumableOptions.SectionName));
            Weapons = new WeaponCatalog(Bind<WeaponOptions>(WeaponOptions.SectionName));
            Materials = new MaterialCatalog(Bind<MaterialOptions>(MaterialOptions.SectionName));
            RewardOptions = Bind<RewardOptions>(Configuration.RewardOptions.SectionName).Value;
            Rewards = new RewardCatalog(Options.Create(RewardOptions), Consumables, Weapons, Materials);
            Combat = new MonsterCombatCatalog(Bind<MonsterCombatOptions>(MonsterCombatOptions.SectionName));
            Encounters = new DungeonEncounterCatalog(Bind<DungeonEncounterOptions>(DungeonEncounterOptions.SectionName), Combat, Rewards);
            Exchanges = new DungeonExchangeCatalog(Bind<DungeonExchangeOptions>(DungeonExchangeOptions.SectionName), Materials, Weapons);
        }
    }
}
