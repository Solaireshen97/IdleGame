using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class CoopDropBonusTests
{
    private const string DungeonCode = "slime-field";
    private const string ItemCode = "minor-healing-potion";

    [Theory]
    [InlineData(0.26, true)]
    [InlineData(0.30, false)]
    public void DropChanceUsesRelativeMultiplicationInsteadOfPercentagePointAddition(double roll, bool expectedDrop)
    {
        var catalog = NewCatalog(new FixedRandom(roll));
        var entries = catalog.Roll(DungeonCode, false, 1, 1, "monster:1", 1, 1, dropChanceBonusPercent: 10);
        Assert.Equal(expectedDrop, entries.Any(entry => entry.Kind == "Consumable"));
        Assert.Equal(3, Assert.Single(entries, entry => entry.Kind == "Gold").Quantity);
        Assert.Equal(2, Assert.Single(entries, entry => entry.Kind == "Experience").Quantity);
        if (expectedDrop) Assert.Equal(2, Assert.Single(entries, entry => entry.Kind == "Consumable").Quantity);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(95, true)]
    [InlineData(100, true)]
    public void ZeroAndGuaranteedDropsDoNotSampleRandomAndBoostedChanceCapsAtOneHundred(int chance, bool expectedDrop)
    {
        var random = new FixedRandom(0.999);
        var entries = NewCatalog(random, chance).Roll(DungeonCode, false, 1, 1, "monster:1", 1, 1,
            dropChanceBonusPercent: 40);
        Assert.Equal(expectedDrop, entries.Any(entry => entry.Kind == "Consumable"));
        Assert.Equal(0, random.Calls);
        Assert.Equal(3, Assert.Single(entries, entry => entry.Kind == "Gold").Quantity);
        Assert.Equal(2, Assert.Single(entries, entry => entry.Kind == "Experience").Quantity);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 30)]
    [InlineData(5, 40)]
    [InlineData(6, 40)]
    public async Task AdditionalActualAccountsIncreaseBonusAndRespectConfiguredMaximum(int accounts, int bonus)
    {
        await using var test = await Scenario.CreateAsync(Enumerable.Range(1, accounts).ToArray());
        Assert.True(await test.Rewards.RecordAsync(test.Room, DungeonCode, test.Participants, "monster:1", false,
            test.CharacterIds));
        await test.Db.SaveChangesAsync();
        await using var fresh = test.OpenFreshContext();
        var evidence = await fresh.RewardEvents.SingleAsync();
        Assert.Equal(accounts, evidence.CoopParticipantCount);
        Assert.Equal((decimal)bonus, evidence.CoopDropBonusPercent);
        Assert.Equal(accounts == 1 ? 0 : accounts,
            await fresh.RewardEntries.CountAsync(entry => entry.Kind == "Consumable"));
        Assert.Equal(accounts * 3, await fresh.RewardEntries.Where(entry => entry.Kind == "Gold").SumAsync(entry => entry.Quantity));
        Assert.Equal(accounts * 2, await fresh.RewardEntries.Where(entry => entry.Kind == "Experience").SumAsync(entry => entry.Quantity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleCharactersOfOneAccountDoNotStackAndDuplicateEvidenceDoesNotStack(bool includeAnotherAccount)
    {
        await using var test = await Scenario.CreateAsync(includeAnotherAccount ? [1, 1, 2] : [1, 1]);
        await test.Rewards.RecordAsync(test.Room, DungeonCode, test.Participants.Concat(test.Participants), "monster:1", false,
            test.CharacterIds.Concat(test.CharacterIds).ToArray());
        await test.Db.SaveChangesAsync();
        await using var fresh = test.OpenFreshContext();
        var evidence = await fresh.RewardEvents.SingleAsync();
        Assert.Equal(includeAnotherAccount ? 2 : 1, evidence.CoopParticipantCount);
        Assert.Equal(includeAnotherAccount ? 10m : 0m, evidence.CoopDropBonusPercent);
        Assert.Equal(includeAnotherAccount ? 3 : 0,
            await fresh.RewardEntries.CountAsync(entry => entry.Kind == "Consumable"));
        Assert.Equal(test.Participants.Count, await fresh.RewardEntries.CountAsync(entry => entry.Kind == "Gold"));
    }

    [Theory]
    [InlineData("missing", 0)]
    [InlineData("empty", 0)]
    [InlineData("unknown", 0)]
    [InlineData("one", 1)]
    public async Task MissingOrInsufficientActualEvidencePreservesBaseRewardsWithoutCoopBonus(string proof, int count)
    {
        await using var test = await Scenario.CreateAsync([1, 2]);
        int[]? actual = proof switch { "missing" => null, "empty" => [], "unknown" => [99], _ => [1] };
        await test.Rewards.RecordAsync(test.Room, DungeonCode, test.Participants, "monster:1", false, actual);
        await test.Db.SaveChangesAsync();
        await using var fresh = test.OpenFreshContext();
        var evidence = await fresh.RewardEvents.SingleAsync();
        Assert.Equal(count, evidence.CoopParticipantCount);
        Assert.Equal(0m, evidence.CoopDropBonusPercent);
        Assert.Empty(await fresh.RewardEntries.Where(entry => entry.Kind == "Consumable").ToListAsync());
        Assert.Equal(2, await fresh.RewardEntries.CountAsync(entry => entry.Kind == "Gold"));
        Assert.Equal(2, await fresh.RewardEntries.CountAsync(entry => entry.Kind == "Experience"));
    }

    [Fact]
    public async Task DeadActualParticipantCountsButUnparticipatingOccupantDoesNotReceiveOrIncreaseBonus()
    {
        await using var test = await Scenario.CreateAsync([1, 2, 3]);
        test.Participants[1].Character.Hp = 0;
        await test.Rewards.RecordAsync(test.Room, DungeonCode, test.Participants, "monster:1", false, [1, 2, 99]);
        await test.Db.SaveChangesAsync();
        await using var fresh = test.OpenFreshContext();
        var evidence = await fresh.RewardEvents.SingleAsync();
        Assert.Equal(2, evidence.CoopParticipantCount);
        Assert.Equal(10m, evidence.CoopDropBonusPercent);
        Assert.Equal(new[] { 1, 2 }, await fresh.RewardEntries.Where(entry => entry.Kind == "Consumable")
            .OrderBy(entry => entry.CharacterId).Select(entry => entry.CharacterId).ToArrayAsync());
        Assert.Equal(3, await fresh.RewardEntries.CountAsync(entry => entry.Kind == "Gold"));
        Assert.Equal(0, (await fresh.Characters.FindAsync(2))!.Hp);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task InvalidClaimedUserIdCannotManufactureAnotherAccount(int claimedUserId)
    {
        await using var test = await Scenario.CreateAsync([1, 1]);
        RewardParticipant[] claimed = [test.Participants[0], new(claimedUserId, test.Participants[1].Character)];
        await test.Rewards.RecordAsync(test.Room, DungeonCode, claimed, "monster:1", false, test.CharacterIds);
        await test.Db.SaveChangesAsync();
        await using var fresh = test.OpenFreshContext();
        var evidence = await fresh.RewardEvents.SingleAsync();
        Assert.Equal(1, evidence.CoopParticipantCount);
        Assert.Equal(0m, evidence.CoopDropBonusPercent);
        Assert.Empty(await fresh.RewardEntries.Where(entry => entry.Kind == "Consumable").ToListAsync());
    }

    [Fact]
    public async Task DuplicateEventDoesNotRerollOrRewriteEvidenceBeforeOrAfterContextRestart()
    {
        await using var test = await Scenario.CreateAsync([1, 2, 3, 4, 5]);
        Assert.True(await test.Rewards.RecordAsync(test.Room, DungeonCode, test.Participants, "monster:1", false, [1, 2]));
        var calls = test.Random.Calls;
        Assert.False(await test.Rewards.RecordAsync(test.Room, DungeonCode, test.Participants, "monster:1", false,
            test.CharacterIds));
        Assert.Equal(calls, test.Random.Calls);
        await test.Db.SaveChangesAsync();
        await using var fresh = test.OpenFreshContext();
        var room = await fresh.Rooms.SingleAsync();
        var characters = await fresh.Characters.OrderBy(character => character.Id).ToListAsync();
        var rewards = new RewardService(fresh, test.Catalog, ProgressionTestFactory.Create());
        Assert.False(await rewards.RecordAsync(room, DungeonCode, characters.Select(character => new RewardParticipant(character.UserId, character)),
            "monster:1", false, test.CharacterIds));
        await fresh.SaveChangesAsync();
        Assert.Equal(calls, test.Random.Calls);
        var evidence = await fresh.RewardEvents.SingleAsync();
        Assert.Equal(2, evidence.CoopParticipantCount);
        Assert.Equal(10m, evidence.CoopDropBonusPercent);
        Assert.Equal(12, await fresh.RewardEntries.CountAsync()); // Ten currency entries and two item entries.
    }

    [Fact]
    public async Task FirstClearIsNotBoostedEvenWithValidActualParticipationEvidence()
    {
        await using var test = await Scenario.CreateAsync([1, 2]);
        await test.Rewards.RecordAsync(test.Room, DungeonCode + "-first-clear", test.Participants, "first-clear", true,
            test.CharacterIds);
        await test.Db.SaveChangesAsync();
        await using var fresh = test.OpenFreshContext();
        var evidence = await fresh.RewardEvents.SingleAsync();
        Assert.Equal(0, evidence.CoopParticipantCount);
        Assert.Equal(0m, evidence.CoopDropBonusPercent);
        Assert.Empty(await fresh.RewardEntries.Where(entry => entry.Kind == "Consumable").ToListAsync());
        Assert.Equal(2, await fresh.RewardEntries.CountAsync(entry => entry.Kind == "Gold"));
    }

    [Fact]
    public async Task MasteryAdditionalItemRollUsesOriginalDropChance()
    {
        await using var test = await Scenario.CreateAsync([1, 2]);
        await test.EnableDepthAsync(mastery: 3, extraRollChance: 100, challengeChance: 0);
        await test.Rewards.RecordAsync(test.Room, DungeonCode, test.Participants, "monster:1", false, test.CharacterIds);
        await test.Db.SaveChangesAsync();
        await using var fresh = test.OpenFreshContext();
        var items = await fresh.RewardEntries.Where(entry => entry.Kind == "Consumable").ToListAsync();
        Assert.Equal(2, items.Count);
        Assert.All(items, entry => Assert.Equal("Base", entry.RewardSource));
        Assert.Equal(4, test.Random.Calls); // Each character's boosted base roll and unboosted mastery roll.
    }

    [Fact]
    public async Task ChallengeFragmentChanceDoesNotReceiveCoopMultiplier()
    {
        await using var test = await Scenario.CreateAsync([1, 2]);
        await test.EnableDepthAsync(mastery: 0, extraRollChance: 0, challengeChance: 25);
        await test.Rewards.RecordAsync(test.Room, DungeonCode, test.Participants, "clear", true, test.CharacterIds);
        await test.Db.SaveChangesAsync();
        await using var fresh = test.OpenFreshContext();
        Assert.Equal(2, await fresh.RewardEntries.CountAsync(entry => entry.Kind == "Consumable"));
        Assert.Empty(await fresh.RewardEntries.Where(entry => entry.RewardSource == "Challenge").ToListAsync());
        Assert.Equal(4, test.Random.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoomSnapshotFreezesCoopPolicyAndLegacyJsonWithoutPolicyKeepsZeroBonus(bool legacyJson)
    {
        await using var test = await Scenario.CreateAsync([1, 2], randomValue: 0.28);
        var rules = NewRunRules(test.Db, test.Catalog);
        await rules.EnsureAsync(test.Room);
        await test.Db.SaveChangesAsync();
        if (legacyJson)
        {
            var row = await test.Db.DungeonRunRuleSnapshots.SingleAsync();
            var json = JsonNode.Parse(row.DefinitionJson)!;
            Assert.True(json["Rewards"]!.AsObject().Remove("CoopDropBonus"));
            row.DefinitionJson = json.ToJsonString();
            row.Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.DefinitionJson))).ToLowerInvariant();
            await test.Db.SaveChangesAsync();
        }
        await using var fresh = test.OpenFreshContext();
        var room = await fresh.Rooms.SingleAsync();
        var characters = await fresh.Characters.OrderBy(character => character.Id).ToListAsync();
        var changedCatalog = NewCatalog(new FixedRandom(0.28), perAdditionalUser: 30, maximum: 90);
        var frozenRules = NewRunRules(fresh, changedCatalog);
        var definition = await frozenRules.EnsureAsync(room);
        Assert.Equal(legacyJson ? 0m : 10m, definition.Rewards.CoopDropBonus.PercentPerAdditionalUser);
        Assert.Equal(legacyJson ? 0m : 40m, definition.Rewards.CoopDropBonus.MaximumPercent);
        var rewards = new RewardService(fresh, changedCatalog, ProgressionTestFactory.Create(), runRules: frozenRules);
        await rewards.RecordAsync(room, DungeonCode, characters.Select(character => new RewardParticipant(character.UserId, character)),
            "monster:1", false, test.CharacterIds);
        await fresh.SaveChangesAsync();
        Assert.Equal(legacyJson ? 0m : 10m, (await fresh.RewardEvents.SingleAsync()).CoopDropBonusPercent);
        Assert.Empty(await fresh.RewardEntries.Where(entry => entry.Kind == "Consumable").ToListAsync());
        // A new room captures the changed policy and can drop at the same random threshold.
        var newRoom = new Room { DungeonId = room.DungeonId, MonsterId = room.MonsterId, SlotCount = 5 };
        fresh.Rooms.Add(newRoom);
        await fresh.SaveChangesAsync();
        await rewards.RecordAsync(newRoom, DungeonCode, characters.Select(character => new RewardParticipant(character.UserId, character)),
            "monster:1", false, test.CharacterIds);
        await fresh.SaveChangesAsync();
        Assert.Equal(30m, (await fresh.RewardEvents.SingleAsync(entry => entry.RoomId == newRoom.Id)).CoopDropBonusPercent);
        Assert.Equal(2, await fresh.RewardEntries.CountAsync(entry => entry.RoomId == newRoom.Id && entry.Kind == "Consumable"));
    }

    [Fact]
    public async Task DungeonCompletionPassesMonsterEvidenceToKillAndRunEvidenceToClear()
    {
        await using var test = await Scenario.CreateAsync([1, 2, 3, 4]);
        var rules = NewRunRules(test.Db, test.Catalog);
        var rewards = new RewardService(test.Db, test.Catalog, ProgressionTestFactory.Create(), runRules: rules);
        var runs = new DungeonRunService(test.Db, rewards, runRules: rules);
        var result = await runs.AdvanceAfterDefeatAsync(test.Room, test.Monster, test.Participants, DateTime.UtcNow, [],
            actualMonsterCharacterIds: [1, 2], actualRunCharacterIds: [1, 2, 3]);
        Assert.Null(result.Error);
        Assert.True(result.IsDungeonComplete);
        await test.Db.SaveChangesAsync();
        await using var fresh = test.OpenFreshContext();
        var kill = await fresh.RewardEvents.SingleAsync(entry => entry.EventKey == "monster:1:1");
        var clear = await fresh.RewardEvents.SingleAsync(entry => entry.EventKey == "clear");
        var first = await fresh.RewardEvents.SingleAsync(entry => entry.EventKey == "first-clear");
        Assert.Equal((2, 10m), (kill.CoopParticipantCount, kill.CoopDropBonusPercent));
        Assert.Equal((3, 20m), (clear.CoopParticipantCount, clear.CoopDropBonusPercent));
        Assert.Equal((0, 0m), (first.CoopParticipantCount, first.CoopDropBonusPercent));
        Assert.Equal(new[] { 1, 2 }, await fresh.RewardEntries.Where(entry => entry.EventKey == kill.EventKey && entry.Kind == "Consumable")
            .OrderBy(entry => entry.CharacterId).Select(entry => entry.CharacterId).ToArrayAsync());
        Assert.Equal(new[] { 1, 2, 3 }, await fresh.RewardEntries.Where(entry => entry.EventKey == "clear" && entry.Kind == "Consumable")
            .OrderBy(entry => entry.CharacterId).Select(entry => entry.CharacterId).ToArrayAsync());
        Assert.Equal("Victory", (await fresh.RewardRuns.SingleAsync()).Status);
        Assert.Equal(9, (await fresh.Characters.FindAsync(4))!.Gold); // CurrentSlots retains ordinary currency eligibility.
    }

    [Fact]
    public async Task EvidenceMigrationPreservesOldEventsDefaultsToZeroAndCanBeRolledBack()
    {
        const string previous = "20260930020000_AddDungeonRunRuleSnapshots";
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options;
        await using (var legacy = new GameDbContext(options))
        {
            await legacy.GetService<IMigrator>().MigrateAsync(previous);
            await legacy.Database.ExecuteSqlRawAsync("INSERT INTO RewardEvents (RoomId, Sequence, EventKey) VALUES (7, 3, 'monster:1:1')");
            await legacy.Database.ExecuteSqlRawAsync("INSERT INTO RewardEntries (RoomId, Sequence, EventKey, UserId, CharacterId, Kind, Code, Quantity, RewardSource) VALUES (7, 3, 'monster:1:1', 1, 1, 'Gold', '', 9, 'Base')");
        }
        await using (var current = new GameDbContext(options))
        {
            await current.Database.MigrateAsync();
            Assert.False(current.Database.HasPendingModelChanges());
            var evidence = await current.RewardEvents.SingleAsync();
            Assert.Equal((7, 3, "monster:1:1", 0, 0m),
                (evidence.RoomId, evidence.Sequence, evidence.EventKey, evidence.CoopParticipantCount, evidence.CoopDropBonusPercent));
            evidence.CoopParticipantCount = 3;
            evidence.CoopDropBonusPercent = 20;
            await current.SaveChangesAsync();
        }
        await using (var downgrade = new GameDbContext(options))
        {
            await downgrade.GetService<IMigrator>().MigrateAsync(previous);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT EventKey FROM RewardEvents WHERE RoomId = 7 AND Sequence = 3";
            Assert.Equal("monster:1:1", await command.ExecuteScalarAsync());
            command.CommandText = "SELECT Quantity FROM RewardEntries WHERE RoomId = 7 AND Sequence = 3";
            Assert.Equal(9L, await command.ExecuteScalarAsync());
            await downgrade.Database.MigrateAsync();
            Assert.False(downgrade.Database.HasPendingModelChanges());
        }
        await using var verify = new GameDbContext(options);
        var restored = await verify.RewardEvents.SingleAsync();
        Assert.Equal((0, 0m), (restored.CoopParticipantCount, restored.CoopDropBonusPercent));
        Assert.Equal(9, (await verify.RewardEntries.SingleAsync()).Quantity);
    }

    private static DungeonRunRulesService NewRunRules(GameDbContext db, RewardCatalog rewards) => new(db,
        new MonsterCombatCatalog(Options.Create(new MonsterCombatOptions())), rewards, PartyScalingCatalog.Default,
        new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions())));

    private static RewardCatalog NewCatalog(Random random, decimal chance = 25, decimal perAdditionalUser = 10, decimal maximum = 40)
    {
        RewardBundleOptions Bundle() => new()
        {
            Gold = 3, Experience = 2,
            Drops = [new() { Kind = "Consumable", Code = ItemCode, Quantity = 2, ChancePercent = chance }]
        };
        return new(Options.Create(new RewardOptions
        {
            AllowConsumableDrops = true,
            CoopDropBonus = new() { PercentPerAdditionalUser = perAdditionalUser, MaximumPercent = maximum },
            MonsterKills = new() { [DungeonCode] = Bundle() },
            DungeonClears = new() { [DungeonCode] = Bundle(), [DungeonCode + "-first-clear"] = Bundle() }
        }), ConsumableTestFactory.Create(), new WeaponCatalog(Options.Create(new WeaponOptions
        {
            Items = [new() { Code = "test-weapon", Name = "Test", Attack = 1, MaxHp = 1 }],
            StarterPacks = new() { ["knight"] = ["test-weapon"] }
        })), random: random);
    }

    private sealed class FixedRandom(double value) : Random
    {
        public int Calls { get; private set; }
        public override double NextDouble() { Calls++; return value; }
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<GameDbContext> _options;
        public GameDbContext Db { get; }
        public Room Room { get; }
        public Monster Monster { get; }
        public List<RewardParticipant> Participants { get; }
        public int[] CharacterIds => Participants.Select(participant => participant.Character.Id).ToArray();
        public FixedRandom Random { get; }
        public RewardCatalog Catalog { get; }
        public RewardService Rewards { get; }

        private Scenario(SqliteConnection connection, DbContextOptions<GameDbContext> options, GameDbContext db,
            Room room, Monster monster, List<RewardParticipant> participants, FixedRandom random)
        {
            (_connection, _options, Db, Room, Monster, Participants, Random) = (connection, options, db, room, monster, participants, random);
            Catalog = NewCatalog(random);
            Rewards = new RewardService(db, Catalog, ProgressionTestFactory.Create());
        }

        public GameDbContext OpenFreshContext() => new(_options);

        public static async Task<Scenario> CreateAsync(int[] ownerIds, double randomValue = 0.26)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options;
            var db = new GameDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var room = new Room { Id = 1, DungeonId = 1, MonsterId = 1, SlotCount = 5 };
            var monster = new Monster { Id = 1, RoomId = 1, WaveNumber = 1, Position = 1, Name = "Boss", Hp = 0,
                MaxHp = 100, BaseMaxHp = 100, Attack = 1, RewardProfileCode = DungeonCode };
            db.Dungeons.Add(new Dungeon { Id = 1, Code = DungeonCode, Name = "Test", DungeonKind = "Dungeon" });
            db.Rooms.Add(room);
            db.Monsters.Add(monster);
            db.Users.AddRange(ownerIds.Distinct().Select(id => new User { Id = id, UserName = $"User{id}", PasswordHash = "test" }));
            var participants = ownerIds.Select((owner, index) => new RewardParticipant(owner,
                new Character { Id = index + 1, UserId = owner, Name = $"Role{index + 1}", Hp = 100, MaxHp = 100 })).ToList();
            db.Characters.AddRange(participants.Select(participant => participant.Character));
            await db.SaveChangesAsync();
            return new Scenario(connection, options, db, room, monster, participants, new FixedRandom(randomValue));
        }

        public async Task EnableDepthAsync(int mastery, decimal extraRollChance, decimal challengeChance)
        {
            Room.DepthLevel = 5;
            Room.DepthDefinitionJson = JsonSerializer.Serialize(new DungeonDepthDefinitionOptions
            {
                GoldBonusPercent = 0, KillExtraRollChancePercent = extraRollChance, ClearExtraRollChancePercent = extraRollChance,
                ChallengeFragmentCode = "challenge-fragment", ChallengeFragmentChancePercent = challengeChance,
                ChallengeStartDepth = 5
            });
            if (mastery > 0) Db.CharacterDungeonProgress.AddRange(Participants.Select(participant => new CharacterDungeonProgress
                { CharacterId = participant.Character.Id, DungeonId = Room.DungeonId, HighestDepth = mastery }));
            await Db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }
}
