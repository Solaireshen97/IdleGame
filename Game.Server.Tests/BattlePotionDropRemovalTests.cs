using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class BattlePotionDropRemovalTests
{
    [Fact]
    public void ProductionRewardsAndPreviewsContainNoConsumables()
    {
        var content = new Content();
        var options = content.Bind<RewardOptions>(RewardOptions.SectionName).Value;
        Assert.False(options.AllowConsumableDrops);
        foreach (var (profiles, isClear) in new[] { (options.MonsterKills, false), (options.DungeonClears, true) })
        foreach (var (code, bundle) in profiles)
        {
            Assert.DoesNotContain(bundle.Drops, drop => drop.Kind == "Consumable");
            Assert.DoesNotContain(content.Live.GetDropPreview(code, isClear), drop => drop.Kind == "Consumable");
        }
    }

    [Theory]
    [InlineData("ragefire-heart-pack", false)]
    [InlineData("ragefire-heart", true)]
    [InlineData("ragefire-heart-first-clear", true)]
    public void LivePolicyBlocksLegacyFrozenPotionsAndPreservesOtherRewards(string code, bool isClear)
    {
        var content = new Content();
        var legacy = content.Legacy();
        var frozen = legacy.CaptureRules("ragefire-heart", ["ragefire-heart-pack"]);
        var oldEntries = legacy.Roll(code, isClear, 1, 1, "test", 1, 1, frozen, 40);
        var newEntries = content.Live.Roll(code, isClear, 1, 1, "test", 1, 1, frozen, 40);
        Assert.Single(oldEntries, entry => entry.Kind == "Consumable");
        Assert.DoesNotContain(newEntries, entry => entry.Kind == "Consumable");
        Assert.Equal(oldEntries.Where(entry => entry.Kind != "Consumable").Select(Key), newEntries.Select(Key));
        Assert.Contains((isClear ? frozen.Clears : frozen.Kills)[code].Drops, drop => drop.Kind == "Consumable");

        static (string, string, int) Key(RewardEntry entry) => (entry.Kind, entry.Code, entry.Quantity);
    }

    [Fact]
    public async Task RestartedOldRoomStopsNewPotionRewardsButSettlesAlreadyEarnedPotions()
    {
        var content = new Content();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var character = new Character { Id = 1, UserId = 1, Name = "Farmer", Level = 10, Hp = 100, MaxHp = 100 };
        var room = new Room { Id = 1, DungeonId = 1, MonsterId = 1, RunSequence = 1, SlotCount = 5 };
        db.AddRange(new User { Id = 1, UserName = "owner", ActiveCharacterId = 1 }, character, room,
            new Dungeon { Id = 1, Code = "ragefire-chasm", Name = "Entry", DungeonKind = "Dungeon" },
            new Monster { Id = 1, RoomId = 1, WaveNumber = 1, Position = 1, Name = "Enemy", Hp = 100,
                MaxHp = 100, BaseMaxHp = 100, Attack = 1, RewardProfileCode = "ragefire-chasm-pack" },
            new CharacterItemStack { CharacterId = 1, ItemCode = "minor-healing-potion", Quantity = 7 });
        await db.SaveChangesAsync();
        DungeonRunRulesService Rules(RewardCatalog catalog) => new(db,
            new MonsterCombatCatalog(Options.Create(new MonsterCombatOptions())), catalog, PartyScalingCatalog.Default,
            new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions())));
        var legacy = content.Legacy();
        var oldRules = Rules(legacy);
        var definition = await oldRules.EnsureAsync(room);
        var oldRewards = new RewardService(db, legacy, ProgressionTestFactory.Create(), runRules: oldRules);
        Assert.True(await oldRewards.RecordAsync(room, "ragefire-chasm-pack", [new(1, character)], "monster:1:1", false));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        room = await db.Rooms.SingleAsync();
        character = await db.Characters.SingleAsync();
        var liveRules = Rules(content.Live);
        Assert.Equal(definition.Revision, (await liveRules.EnsureAsync(room)).Revision);
        var liveRewards = new RewardService(db, content.Live, ProgressionTestFactory.Create(), runRules: liveRules);
        Assert.True(await liveRewards.RecordAsync(room, "ragefire-chasm", [new(1, character)], "clear", true));
        await liveRewards.SettleAsync(room, true, DateTime.UtcNow, []);
        await db.SaveChangesAsync();
        Assert.False(await db.RewardEntries.AnyAsync(entry => entry.EventKey == "clear" && entry.Kind == "Consumable"));
        Assert.Single(await db.RewardEntries.Where(entry => entry.Kind == "Consumable").ToListAsync());
        Assert.Equal(9, (await db.CharacterItemStacks.SingleAsync(stack => stack.ItemCode == "minor-healing-potion")).Quantity);
        Assert.Equal(23, character.Gold);
        // Settlement remains idempotent for the legacy reward that was earned before the change.
        await liveRewards.SettleAsync(room, true, DateTime.UtcNow, []);
        await db.SaveChangesAsync();
        Assert.Equal(9, (await db.CharacterItemStacks.SingleAsync(stack => stack.ItemCode == "minor-healing-potion")).Quantity);
    }

    private sealed class Content
    {
        private readonly IConfiguration _configuration = new ConfigurationBuilder()
            .AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        public RewardCatalog Live => Catalog(Bind<RewardOptions>(RewardOptions.SectionName).Value);
        public IOptions<T> Bind<T>(string section) where T : class, new() => Options.Create(_configuration.GetSection(section).Get<T>()!);
        public RewardCatalog Legacy()
        {
            var options = Bind<RewardOptions>(RewardOptions.SectionName).Value;
            options.AllowConsumableDrops = true;
            options.UseLiveGoldRewards = false;
            options.DungeonClears["ragefire-chasm"].Gold = 130;
            options.DungeonClears["ragefire-chasm-first-clear"].Gold = 100;
            foreach (var bundle in options.MonsterKills.Values.Concat(options.DungeonClears.Values))
                bundle.Drops.Add(new() { Kind = "Consumable", Code = "minor-healing-potion", Quantity = 2, ChancePercent = 100 });
            return Catalog(options);
        }
        private RewardCatalog Catalog(RewardOptions options) => new(Options.Create(options),
            new ConsumableCatalog(Bind<ConsumableOptions>(ConsumableOptions.SectionName)),
            new WeaponCatalog(Bind<WeaponOptions>(WeaponOptions.SectionName)),
            new MaterialCatalog(Bind<MaterialOptions>(MaterialOptions.SectionName)),
            new SoulImprintCatalog(Bind<SoulImprintOptions>(SoulImprintOptions.SectionName)), new HighRollRandom());
    }

    private sealed class HighRollRandom : Random
    {
        public override double NextDouble() => .99;
    }
}
