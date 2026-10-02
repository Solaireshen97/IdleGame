using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class DeepDungeonChallengeTests
{
    [Theory]
    [InlineData("ragefire-heart")]
    [InlineData("frostspring-throne")]
    [InlineData("kobold-mine-depths")]
    [InlineData("windfury-spire")]
    [InlineData("dawn-core")]
    [InlineData("plague-crypt-depths")]
    public void ProductionChallengeUsesLv4AnchorSeparateGrowthAndExistingMechanics(string code)
    {
        var config = new ConfigurationBuilder().AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        T Bind<T>(string section) where T : new() => config.GetSection(section).Get<T>()!;
        var depths = new DungeonDepthCatalog(Options.Create(Bind<DungeonDepthOptions>("DungeonDepths")));
        var combat = new MonsterCombatCatalog(Options.Create(Bind<MonsterCombatOptions>("MonsterCombat")));
        var encounters = new DungeonEncounterCatalog(Options.Create(Bind<DungeonEncounterOptions>("DungeonEncounters")), combat, depthCatalog: depths);
        var definition = depths.Find(code)!;
        Assert.False(definition.UsesPlaceholderBalance);
        Assert.Equal(new[] { 1, 2, 3, 4 }, definition.CalibratedDepths); // Challenge rules are adopted without combat calibration.
        Assert.Equal(15m, definition.ChallengeHpGrowthPercent);
        Assert.Equal(10m, definition.ChallengeAttackGrowthPercent);
        Assert.Equal(100m, definition.ChallengeFragmentChancePercent);
        Assert.Equal("weapon-breakthrough-stone-t1", definition.ChallengeFirstClearItemCode);
        Assert.Equal(10, definition.ChallengeFirstClearQuantities.Values.Sum());
        var dungeon = new Dungeon { Code = code };
        var anchor = encounters.CreateMonsters(dungeon, 4);
        var hpFactor = 1m;
        var attackFactor = 1m;
        for (var depth = 5; depth <= 10; depth++)
        {
            hpFactor *= 1.15m;
            attackFactor *= 1.10m;
            var monsters = encounters.CreateMonsters(dungeon, depth);
            Assert.Equal(5, monsters.Count);
            for (var i = 0; i < monsters.Count; i++)
            {
                Assert.Equal((int)decimal.Ceiling(anchor[i].MaxHp * hpFactor), monsters[i].MaxHp);
                Assert.Equal((int)decimal.Ceiling(anchor[i].Attack * attackFactor), monsters[i].Attack);
                Assert.Equal(monsters[i].MaxHp, monsters[i].BaseMaxHp);
                Assert.Equal(anchor[i].CombatProfileCode, monsters[i].CombatProfileCode);
                Assert.Equal(anchor[i].RewardProfileCode, monsters[i].RewardProfileCode);
                Assert.Equal(anchor[i].Defense, monsters[i].Defense);
            }
            Assert.Equal((depth - 4) * 10, definition.ChallengeFragmentsAt(depth));
            Assert.Equal(depth <= 7 ? 1 : depth <= 9 ? 2 : 3, definition.ChallengeFirstClearQuantities[depth]);
        }
    }

    [Theory]
    [InlineData(5, 1)]
    [InlineData(6, 2)]
    [InlineData(7, 3)]
    [InlineData(8, 5)]
    [InlineData(9, 7)]
    [InlineData(10, 10)]
    public async Task JumpClearGrantsAllUnclaimedLayersAndReentryOrNewAttemptCannotDuplicate(int depth, int stones)
    {
        await using var test = await Scenario.CreateAsync();
        var room = await test.WinAsync(depth, [1]);
        Assert.Equal(stones, await test.QuantityAsync(1, "stone"));
        Assert.Equal((depth - 4) * 10, await test.QuantityAsync(1, "fragment"));
        Assert.Equal(depth - 4, await test.Db.CharacterBattleMilestones.CountAsync(m => m.Kind == DungeonDepthProgressService.ChallengeFirstClearKind));
        Assert.All(await test.Db.RewardEntries.Where(e => e.RewardSource == "ChallengeFirstClear").ToListAsync(), e => Assert.Equal("stone", e.Code));
        Assert.All(await test.Db.RewardEvents.Where(e => e.EventKey.StartsWith("challenge-first-clear:")).ToListAsync(), e => Assert.Equal(0, e.CoopDropBonusPercent));
        var boss = (await test.Db.Monsters.FindAsync(room.MonsterId))!;
        await test.Runs.AdvanceAfterDefeatAsync(room, boss, test.Participants, DateTime.UtcNow, [], [1], [1]);
        await test.Db.SaveChangesAsync();
        Assert.Equal(stones, await test.QuantityAsync(1, "stone"));
        test.Db.ChangeTracker.Clear();
        await test.WinAsync(depth, [1]);
        Assert.Equal(stones, await test.QuantityAsync(1, "stone"));
        Assert.Equal((depth - 4) * 20, await test.QuantityAsync(1, "fragment"));
    }

    [Fact]
    public async Task FirstClearIsPerCharacterAndDungeonAndOnlyActualParticipantsReceiveIt()
    {
        await using var test = await Scenario.CreateAsync();
        await test.WinAsync(7, [1]);
        Assert.Equal(3, await test.QuantityAsync(1, "stone"));
        Assert.Equal(0, await test.QuantityAsync(2, "stone")); // Same account, absent from actual participation.
        await test.WinAsync(8, [1, 2]);
        Assert.Equal(5, await test.QuantityAsync(1, "stone"));
        Assert.Equal(5, await test.QuantityAsync(2, "stone"));
        Assert.Equal(0, await test.QuantityAsync(3, "stone"));
        await test.WinAsync(5, [1], dungeonId: 2);
        Assert.Equal(6, await test.QuantityAsync(1, "stone"));
        Assert.Equal(8, (await test.Db.CharacterDungeonProgress.FindAsync(1, 1))!.HighestDepth);
        Assert.Equal(5, (await test.Db.CharacterDungeonProgress.FindAsync(1, 2))!.HighestDepth);
    }

    [Fact]
    public async Task DefeatCannotGrantChallengeOrConsumeFirstClearReceipts()
    {
        await using var test = await Scenario.CreateAsync();
        var room = await test.CreateRoomAsync(10);
        await test.Rewards.RecordAsync(room, "slime-field", test.Participants, "monster:1:1", false, [1]);
        await test.Rewards.RecordChallengeFirstClearsAsync(room, test.Participants, DateTime.UtcNow, []);
        await test.Rewards.SettleAsync(room, false, DateTime.UtcNow, []);
        await test.Db.SaveChangesAsync();
        Assert.Empty(await test.Db.RewardEntries.Where(e => e.RewardSource == "Challenge" || e.RewardSource == "ChallengeFirstClear").ToListAsync());
        Assert.Empty(await test.Db.CharacterBattleMilestones.Where(m => m.Kind == DungeonDepthProgressService.ChallengeFirstClearKind).ToListAsync());
        await test.WinAsync(5, [1]);
        Assert.Equal(1, await test.QuantityAsync(1, "stone"));
    }

    [Fact]
    public async Task MissingFieldsInFrozenLegacyRoomKeepOldRewardsWithoutConsumingNewFirstClear()
    {
        await using var test = await Scenario.CreateAsync();
        const string legacy = "{\"MaximumDepth\":10,\"GrowthPercent\":10,\"ChallengeFragmentCode\":\"fragment\",\"ChallengeFragmentChancePercent\":100,\"ChallengeFragmentQuantity\":1}";
        await test.WinAsync(7, [1], legacyJson: legacy);
        Assert.Equal(1, await test.QuantityAsync(1, "fragment"));
        Assert.Equal(0, await test.QuantityAsync(1, "stone"));
        var old = JsonSerializer.Deserialize<DungeonDepthDefinitionOptions>(legacy)!;
        var depths = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions { Dungeons = new() { ["old"] = old } }));
        Assert.Equal(1.771561m, depths.StatMultiplier("old", 10, 4));
        Assert.Equal(1.771561m, depths.AttackMultiplier("old", 10, 4));
        await test.WinAsync(7, [1]); // Historical highest depth alone is not a claimed reward.
        Assert.Equal(3, await test.QuantityAsync(1, "stone"));
        Assert.Equal(31, await test.QuantityAsync(1, "fragment"));
    }

    [Fact]
    public async Task PreviewShowsSeparateMultipliersAndOnlyUnclaimedFirstClearRewards()
    {
        await using var test = await Scenario.CreateAsync();
        var preview = (await test.Rooms.GetDungeonAsync(1, "test", 8))!;
        var depth = preview.Depths.Single(d => d.DepthLevel == 8);
        Assert.Equal(4, depth.StatBaseDepth);
        Assert.Equal(1.74900625m, depth.StatMultiplier);
        Assert.Equal(1.4641m, depth.AttackMultiplier);
        Assert.Equal(40, Assert.Single(preview.RewardPreview, r => r.Source == "挑战额外奖励").Quantity);
        Assert.Equal(5, Assert.Single(preview.RewardPreview, r => r.Source == "挑战首通及补领").Quantity);
        await test.WinAsync(7, [1]);
        preview = (await test.Rooms.GetDungeonAsync(1, "test", 8))!;
        Assert.Equal(2, Assert.Single(preview.RewardPreview, r => r.Source == "挑战首通及补领").Quantity);
        await test.WinAsync(8, [1]);
        Assert.DoesNotContain((await test.Rooms.GetDungeonAsync(1, "test", 8))!.RewardPreview, r => r.Source == "挑战首通及补领");
    }

    [Fact]
    public async Task LegacyFullSnapshotSurvivesReloadAndRepeatWhileFreshRoomGrantsNewRewards()
    {
        await using var test = await Scenario.CreateAsync();
        const string legacy = "{\"MaximumDepth\":10,\"GrowthPercent\":10,\"ChallengeFragmentCode\":\"fragment\",\"ChallengeFragmentChancePercent\":100,\"ChallengeFragmentQuantity\":1}";
        var room = await test.CreateRoomAsync(7, legacyJson: legacy);
        var depths = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions { Dungeons = new() { ["slime-field"] = Definition() } }));
        var combat = MonsterCombatTestFactory.CreateCatalog();
        var catalog = RewardTestFactory.CreateCatalog();
        var parties = new PartyScalingCatalog(Options.Create(new PartyScalingOptions { Profiles = new() { ["fixed"] = [100, 100, 100, 100, 100] } }));
        var capture = new DungeonRunRulesService(test.Db, combat, catalog, parties, depths);
        await capture.EnsureAsync(room);
        var snapshot = await test.Db.DungeonRunRuleSnapshots.FindAsync(room.Id);
        // A pre-update full snapshot physically omits the newly introduced fields.
        var json = JsonNode.Parse(snapshot!.DefinitionJson)!;
        foreach (var key in new[] { "ChallengeHpGrowthPercent", "ChallengeAttackGrowthPercent", "ChallengeFragmentQuantities", "ChallengeFirstClearItemCode", "ChallengeFirstClearQuantities" })
            json["Depth"]!.AsObject().Remove(key);
        snapshot.DefinitionJson = json.ToJsonString();
        snapshot.Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.DefinitionJson))).ToLowerInvariant();
        var revision = snapshot.Revision;
        var roomId = room.Id;
        await test.Db.SaveChangesAsync();
        test.Db.ChangeTracker.Clear();
        room = (await test.Db.Rooms.FindAsync(roomId))!;
        await test.Db.Characters.LoadAsync();
        var rules = new DungeonRunRulesService(test.Db, combat, catalog, parties, depths);
        Assert.Equal(revision, (await rules.EnsureAsync(room)).Revision);
        var progress = new DungeonDepthProgressService(test.Db, depths, rules);
        var rewards = new RewardService(test.Db, catalog, ProgressionTestFactory.Create(), depthProgress: progress, runRules: rules);
        var runs = new DungeonRunService(test.Db, rewards, depthProgress: progress, runRules: rules);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0)
            {
                room.RunSequence++;
                await rules.RefreshAsync(room);
                Assert.Equal(revision, (await rules.EnsureAsync(room)).Revision);
            }
            var boss = (await test.Db.Monsters.FindAsync(room.MonsterId))!;
            boss.Hp = 0;
            Assert.Null((await runs.AdvanceAfterDefeatAsync(room, boss, test.Participants, DateTime.UtcNow, [], [1], [1])).Error);
            await test.Db.SaveChangesAsync();
        }
        Assert.Equal(2, await test.QuantityAsync(1, "fragment"));
        Assert.Equal(0, await test.QuantityAsync(1, "stone"));
        Assert.Empty(await test.Db.CharacterBattleMilestones.Where(m => m.Kind == DungeonDepthProgressService.ChallengeFirstClearKind).ToListAsync());
        var fresh = await test.CreateRoomAsync(7);
        Assert.Equal(15m, (await rules.EnsureAsync(fresh)).Depth!.ChallengeHpGrowthPercent);
        Assert.Null((await runs.AdvanceAfterDefeatAsync(fresh, (await test.Db.Monsters.FindAsync(fresh.MonsterId))!, test.Participants, DateTime.UtcNow, [], [1], [1])).Error);
        await test.Db.SaveChangesAsync();
        Assert.Equal(32, await test.QuantityAsync(1, "fragment"));
        Assert.Equal(3, await test.QuantityAsync(1, "stone"));
    }

    [Theory]
    [InlineData("hp")]
    [InlineData("attack")]
    [InlineData("fragments")]
    [InlineData("first-depth")]
    [InlineData("first-quantity")]
    [InlineData("first-code")]
    public void InvalidChallengeRulesAreRejected(string change)
    {
        var d = Definition();
        switch (change)
        {
            case "hp": d.ChallengeHpGrowthPercent = -1; break;
            case "attack": d.ChallengeAttackGrowthPercent = -1; break;
            case "fragments": d.ChallengeFragmentQuantities[11] = 70; break;
            case "first-depth": d.ChallengeFirstClearQuantities[4] = 1; break;
            case "first-quantity": d.ChallengeFirstClearQuantities[5] = 0; break;
            case "first-code": d.ChallengeFirstClearItemCode = ""; break;
        }
        Assert.Throws<InvalidOperationException>(() => new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions { Dungeons = new() { ["bad"] = d } })));
    }

    private static DungeonDepthDefinitionOptions Definition() => new()
    {
        MaximumDepth = 10, ChallengeHpGrowthPercent = 15, ChallengeAttackGrowthPercent = 10,
        ChallengeFragmentCode = "fragment", ChallengeFragmentChancePercent = 100, ChallengeFragmentQuantity = 10,
        ChallengeFragmentQuantities = new() { [5] = 10, [6] = 20, [7] = 30, [8] = 40, [9] = 50, [10] = 60 },
        ChallengeFirstClearItemCode = "stone",
        ChallengeFirstClearQuantities = new() { [5] = 1, [6] = 1, [7] = 1, [8] = 2, [9] = 2, [10] = 3 },
        KillExtraRollChancePercent = 100, ClearExtraRollChancePercent = 100
    };

    private sealed class Scenario(SqliteConnection connection, GameDbContext db) : IAsyncDisposable
    {
        public GameDbContext Db { get; } = db;
        public DungeonDepthProgressService Progress { get; private set; } = null!;
        public RewardService Rewards { get; private set; } = null!;
        public DungeonRunService Runs { get; private set; } = null!;
        public RoomService Rooms { get; private set; } = null!;
        public List<RewardParticipant> Participants => Db.Characters.Local.OrderBy(c => c.Id).Select(c => new RewardParticipant(c.UserId, c)).ToList();

        public static async Task<Scenario> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            db.Users.Add(new User { Id = 1, UserName = "owner", ActiveCharacterId = 1 });
            db.UserLoginSessions.Add(new UserLoginSession { UserId = 1, Token = "test", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            for (var id = 1; id <= 3; id++)
            {
                db.Characters.Add(new Character { Id = id, UserId = 1, Name = $"Role{id}", ProfessionCode = "knight", Level = 10, Hp = 1000, MaxHp = 1000 });
                db.CharacterDungeonProgress.Add(new CharacterDungeonProgress { CharacterId = id, DungeonId = 1, HighestDepth = 4 });
            }
            db.Dungeons.AddRange(new Dungeon { Id = 1, Code = "slime-field", DungeonKind = "Elite", IsVisible = true, MinimumLevel = 10, RecommendedLevel = 10, MonsterName = "Boss", MonsterMaxHp = 100, MonsterAttack = 10 },
                new Dungeon { Id = 2, Code = "other", DungeonKind = "Elite", IsVisible = true, MinimumLevel = 10, RecommendedLevel = 10 });
            db.UserDungeonClears.Add(new UserDungeonClear { UserId = 1, DungeonId = 1, HighestDepth = 10 });
            await db.SaveChangesAsync();
            var test = new Scenario(connection, db);
            var depths = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions { Dungeons = new() { ["slime-field"] = Definition(), ["other"] = Definition() } }));
            test.Progress = new(db, depths);
            var catalog = RewardTestFactory.CreateCatalog();
            var progression = ProgressionTestFactory.Create();
            test.Rewards = new(db, catalog, progression, depthProgress: test.Progress);
            test.Runs = new(db, test.Rewards, depthProgress: test.Progress);
            var consumables = ConsumableTestFactory.Create();
            var skills = SkillTestFactory.Create();
            test.Rooms = new(db, new UserService(db, progression, skills), progression, consumables, skills,
                test.Rewards, depthCatalog: depths, depthProgress: test.Progress);
            return test;
        }

        public async Task<Room> CreateRoomAsync(int depth, int dungeonId = 1, string? legacyJson = null)
        {
            await Db.Characters.LoadAsync();
            var room = new Room { DungeonId = dungeonId, OwnerUserId = 1, DepthLevel = depth, SlotCount = 5, RunSequence = 1,
                DepthDefinitionJson = legacyJson ?? JsonSerializer.Serialize(Definition()) };
            Db.Rooms.Add(room);
            await Db.SaveChangesAsync();
            var boss = new Monster { RoomId = room.Id, Name = "Boss", Hp = 0, MaxHp = 100, BaseMaxHp = 100,
                WaveNumber = 1, Position = 1, RewardProfileCode = "slime-field", IsBoss = true };
            Db.Monsters.Add(boss);
            await Db.SaveChangesAsync();
            room.MonsterId = boss.Id;
            return room;
        }

        public async Task<Room> WinAsync(int depth, int[] actual, int dungeonId = 1, string? legacyJson = null)
        {
            var room = await CreateRoomAsync(depth, dungeonId, legacyJson);
            if (dungeonId == 2)
            {
                // Freeze a compatible reward profile while retaining the second dungeon's claim identity.
                await Rewards.RecordAsync(room, "slime-field", Participants.Where(p => actual.Contains(p.Character.Id)), "clear", true, actual);
                await Rewards.RecordChallengeFirstClearsAsync(room, Participants.Where(p => actual.Contains(p.Character.Id)), DateTime.UtcNow, []);
                await Progress.RecordCharacterClearsAsync(room, actual, []);
                await Rewards.SettleAsync(room, true, DateTime.UtcNow, []);
            }
            else
                Assert.Null((await Runs.AdvanceAfterDefeatAsync(room, (await Db.Monsters.FindAsync(room.MonsterId))!, Participants, DateTime.UtcNow, [], actual, actual)).Error);
            await Db.SaveChangesAsync();
            return room;
        }

        public async Task<int> QuantityAsync(int character, string code) => await Db.CharacterItemStacks
            .Where(s => s.CharacterId == character && s.ItemCode == code).Select(s => s.Quantity).SingleOrDefaultAsync();
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
