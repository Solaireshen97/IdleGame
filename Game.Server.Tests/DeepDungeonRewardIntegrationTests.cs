using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Shop;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class DeepDungeonRewardIntegrationTests
{
    [Theory]
    [InlineData("ragefire-heart")]
    [InlineData("frostspring-throne")]
    [InlineData("kobold-mine-depths")]
    [InlineData("windfury-spire")]
    [InlineData("dawn-core")]
    [InlineData("plague-crypt-depths")]
    public async Task ProductionDeepRewardsSettleKillsAndFixedClearMaterialsWithoutDuplicatingOrGrantingOnFailure(string code)
    {
        var content = new Content(0);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var character = new Character { Id = 1, UserId = 1, Name = "Farmer", ProfessionCode = "swordsman", Level = 10, Hp = 1000, MaxHp = 1000 };
        var dungeon = content.World.Dungeons.Single(item => item.Code == code);
        db.AddRange(new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 }, character, dungeon);
        await db.SaveChangesAsync();
        var rules = new DungeonRunRulesService(db, content.Combat, content.Rewards, content.Parties, content.Depths,
            encounters: content.Encounters);
        var progress = new DungeonDepthProgressService(db, content.Depths, rules);
        var rewardService = new RewardService(db, content.Rewards, ProgressionTestFactory.Create(),
            depthProgress: progress, runRules: rules);
        var runs = new DungeonRunService(db, rewardService, depthProgress: progress, runRules: rules);
        var participants = new[] { new RewardParticipant(1, character) };
        var weaponCodes = content.Exchanges.Offers.Where(offer => offer.DungeonCode == code && offer.RewardKind == "Weapon")
            .Select(offer => offer.EffectiveRewardCode).Order().ToArray();
        var currency = content.Exchanges.Offers.First(offer => offer.DungeonCode == code).CurrencyCode;

        var first = await RunAsync(5);
        Assert.Equal(5, await QuantityAsync("weapon-fragment-t1"));
        Assert.Equal(3, await QuantityAsync(currency)); // One normal token and two first-clear tokens.
        Assert.Equal(10, await db.CharacterWeapons.CountAsync());
        Assert.All(await db.CharacterWeapons.Include(weapon => weapon.Skills).ToListAsync(), weapon =>
        {
            Assert.Contains(weapon.WeaponCode, weaponCodes);
            Assert.Equal(0, weapon.QualityRank);
            Assert.Equal(2, weapon.Skills.Count);
            Assert.All(weapon.Skills, skill => Assert.Equal(1, skill.Level));
        });
        Assert.DoesNotContain(await db.RewardEntries.Where(entry => entry.RoomId == first.Id).ToListAsync(),
            entry => entry.Kind == "Weapon" && entry.EventKey == "clear");
        Assert.Single(await db.CharacterSoulImprints.ToListAsync());

        // Reprocessing the last enemy must not pay the clear or first-clear reward again.
        var boss = await db.Monsters.Where(monster => monster.RoomId == first.Id).OrderBy(monster => monster.WaveNumber)
            .ThenBy(monster => monster.Position).LastAsync();
        Assert.Null((await runs.AdvanceAfterDefeatAsync(first, boss, participants, DateTime.UtcNow, [], [1], [1])).Error);
        await db.SaveChangesAsync();
        Assert.Equal(5, await QuantityAsync("weapon-fragment-t1"));
        Assert.Equal(3, await QuantityAsync(currency));
        Assert.Equal(10, await db.CharacterWeapons.CountAsync());

        await RunAsync(5);
        Assert.Equal(10, await QuantityAsync("weapon-fragment-t1"));
        Assert.Equal(4, await QuantityAsync(currency));
        Assert.Equal(20, await db.CharacterWeapons.CountAsync());
        var failed = await RunAsync(4);
        Assert.Equal(10, await QuantityAsync("weapon-fragment-t1"));
        Assert.Equal(4, await QuantityAsync(currency));
        Assert.Equal(28, await db.CharacterWeapons.CountAsync());
        Assert.Equal("Defeat", (await db.RewardRuns.SingleAsync(run => run.RoomId == failed.Id)).Status);
        Assert.DoesNotContain(await db.RewardEntries.Where(entry => entry.RoomId == failed.Id).ToListAsync(),
            entry => entry.EventKey is "clear" or "first-clear");

        Task<int> QuantityAsync(string itemCode) => db.CharacterItemStacks.Where(stack => stack.CharacterId == 1 && stack.ItemCode == itemCode)
            .Select(stack => stack.Quantity).SingleAsync();

        async Task<Room> RunAsync(int kills)
        {
            var room = new Room { DungeonId = dungeon.Id, OwnerUserId = 1, SlotCount = 5, DepthLevel = 1,
                Status = RoomStatus.Preparing, CurrentWaveNumber = 1, RunSequence = 1 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            var monsters = content.Encounters.CreateMonsters(dungeon).ToList();
            foreach (var monster in monsters) monster.RoomId = room.Id;
            db.Monsters.AddRange(monsters);
            await db.SaveChangesAsync();
            room.MonsterId = monsters[0].Id;
            room.TotalWaveCount = monsters.Max(monster => monster.WaveNumber);
            await rules.EnsureAsync(room);
            await db.SaveChangesAsync();
            for (var index = 0; index < kills; index++)
            {
                monsters[index].Hp = 0;
                var result = await runs.AdvanceAfterDefeatAsync(room, monsters[index], participants, DateTime.UtcNow, [], [1], [1]);
                Assert.Null(result.Error);
                Assert.Equal(index == 4, result.IsDungeonComplete);
                await db.SaveChangesAsync();
            }
            if (kills < 5)
            {
                await rewardService.SettleAsync(room, false, DateTime.UtcNow, []);
                room.Status = RoomStatus.BattleOver;
                await db.SaveChangesAsync();
            }
            return room;
        }
    }

    [Theory]
    [InlineData("ragefire-heart")]
    [InlineData("frostspring-throne")]
    [InlineData("kobold-mine-depths")]
    [InlineData("windfury-spire")]
    [InlineData("dawn-core")]
    [InlineData("plague-crypt-depths")]
    public async Task ProductionWeaponExchangesRequireSixtyTokensAndDeliverEachNewWeaponOnlyOnce(string code)
    {
        var content = new Content(.99);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var character = new Character { Id = 1, UserId = 1, Name = "Farmer", ProfessionCode = "swordsman", Level = 10, Hp = 1000, MaxHp = 1000 };
        db.AddRange(new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 }, character,
            new UserLoginSession { UserId = 1, Token = "deep-reward-test", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
        var offers = content.Exchanges.Offers.Where(offer => offer.DungeonCode == code && offer.RewardKind == "Weapon").ToList();
        db.CharacterItemStacks.Add(new CharacterItemStack { CharacterId = 1, ItemCode = offers[0].CurrencyCode, Quantity = 59 });
        await db.SaveChangesAsync();
        var shop = new ShopCatalog(content.Bind<ShopOptions>(ShopOptions.SectionName), content.Consumables, content.Weapons,
            new PlantingCatalog(content.Bind<PlantingOptions>(PlantingOptions.SectionName)), content.Materials);
        var service = new ShopService(db, new UserService(db, ProgressionTestFactory.Create(), SkillTestFactory.Create()),
            shop, content.Consumables, content.Weapons, content.Materials, content.Exchanges, content.Souls);

        foreach (var offer in offers)
        {
            var wallet = await db.CharacterItemStacks.SingleAsync(stack => stack.ItemCode == offer.CurrencyCode);
            wallet.Quantity = 59;
            wallet.Version++;
            await db.SaveChangesAsync();
            var beforeCount = await db.CharacterWeapons.CountAsync();
            var request = new ExchangeDungeonWeaponRequest { CharacterId = 1, OfferCode = offer.Code, RequestId = Guid.NewGuid().ToString("N") };
            Assert.Equal("InsufficientDungeonCurrency", (await service.ExchangeAsync("deep-reward-test", request)).Error);
            Assert.Equal(beforeCount, await db.CharacterWeapons.CountAsync());
            wallet = await db.CharacterItemStacks.SingleAsync(stack => stack.ItemCode == offer.CurrencyCode);
            Assert.Equal(59, wallet.Quantity);
            wallet.Quantity = 60;
            wallet.Version++;
            await db.SaveChangesAsync();
            Assert.Null((await service.ExchangeAsync("deep-reward-test", request)).Error);
            Assert.Null((await service.ExchangeAsync("deep-reward-test", request)).Error);
            Assert.Equal(0, await db.CharacterItemStacks.Where(stack => stack.ItemCode == offer.CurrencyCode).Select(stack => stack.Quantity).SingleAsync());
            var weapon = Assert.Single(await db.CharacterWeapons.Include(weapon => weapon.Skills)
                .Where(weapon => weapon.WeaponCode == offer.EffectiveRewardCode).ToListAsync());
            Assert.Equal(WeaponOrigin.Exchange, weapon.Origin);
            Assert.Equal(0, weapon.QualityRank);
            Assert.Equal(2, weapon.Skills.Count);
        }
    }

    [Theory]
    [InlineData(.005, 2, 2)]
    [InlineData(.01, 0, 2)]
    [InlineData(.03, 0, 2)]
    [InlineData(.05, 0, 0)]
    public void ProductionRollsUseOnePercentForRegularEnemiesAndFivePercentForBoss(double roll, int regularCount, int bossCount)
    {
        var content = new Content(roll);
        Assert.Equal(regularCount, content.Rewards.Roll("ragefire-heart-pack", false, 1, 1, "monster:1:1", 1, 1).Count(entry => entry.Kind == "Weapon"));
        Assert.Equal(bossCount, content.Rewards.Roll("ragefire-heart-boss", false, 1, 1, "monster:5:1", 1, 1).Count(entry => entry.Kind == "Weapon"));
    }

    private sealed class Content
    {
        private readonly IConfiguration _configuration = new ConfigurationBuilder()
            .AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        public Content(double roll)
        {
            Weapons = new(Bind<WeaponOptions>(WeaponOptions.SectionName));
            Consumables = new(Bind<ConsumableOptions>(ConsumableOptions.SectionName));
            Materials = new(Bind<MaterialOptions>(MaterialOptions.SectionName));
            Souls = new(Bind<SoulImprintOptions>(SoulImprintOptions.SectionName));
            Rewards = new(Bind<RewardOptions>(RewardOptions.SectionName), Consumables, Weapons, Materials, Souls, new FixedRandom(roll));
            Combat = new(Bind<MonsterCombatOptions>(MonsterCombatOptions.SectionName));
            Depths = new(Bind<DungeonDepthOptions>(DungeonDepthOptions.SectionName));
            Parties = new(Bind<PartyScalingOptions>(PartyScalingOptions.SectionName));
            Encounters = new(Bind<DungeonEncounterOptions>(DungeonEncounterOptions.SectionName), Combat, Rewards, Depths);
            Exchanges = new(Bind<DungeonExchangeOptions>(DungeonExchangeOptions.SectionName), Materials, Weapons, Souls);
        }
        public IOptions<T> Bind<T>(string section) where T : class, new() => Options.Create(_configuration.GetSection(section).Get<T>()!);
        public WorldCatalog World { get; } = WorldCatalog.LoadDefault();
        public WeaponCatalog Weapons { get; }
        public ConsumableCatalog Consumables { get; }
        public MaterialCatalog Materials { get; }
        public SoulImprintCatalog Souls { get; }
        public RewardCatalog Rewards { get; }
        public MonsterCombatCatalog Combat { get; }
        public DungeonDepthCatalog Depths { get; }
        public PartyScalingCatalog Parties { get; }
        public DungeonEncounterCatalog Encounters { get; }
        public DungeonExchangeCatalog Exchanges { get; }
    }

    private sealed class FixedRandom(double roll) : Random
    {
        public override double NextDouble() => roll;
    }
}
