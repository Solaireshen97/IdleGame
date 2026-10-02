using System.Text.Json;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class DungeonMasteryRewardTests
{
    [Fact]
    public async Task AdditionalRollCanDropAnItemEvenWhenTheBaseRollDropsNothing()
    {
        await using var test = await Scenario.CreateAsync(3);
        var weapons = new WeaponCatalog(Options.Create(new WeaponOptions
        {
            Items = [new() { Code = "test-weapon", Name = "Test", MaxHp = 1 }],
            StarterPacks = new() { ["swordsman"] = ["test-weapon"] }
        }));
        var catalog = new RewardCatalog(Options.Create(new RewardOptions
        {
            AllowConsumableDrops = true,
            MonsterKills = new() { ["slime-field"] = new()
            {
                Drops = [new() { Kind = "Consumable", Code = "minor-healing-potion", ChancePercent = 25 }]
            } }
        }), ConsumableTestFactory.Create(), weapons, random: new SequenceRandom(0.9, 0.1));
        var rewards = new RewardService(test.Db, catalog, ProgressionTestFactory.Create(), depthProgress: test.Progress);
        await rewards.RecordAsync(test.Room, "slime-field", test.Participants, "monster:1", false);
        await test.Db.SaveChangesAsync();
        var entry = Assert.Single(await test.Db.RewardEntries.ToListAsync());
        Assert.Equal("Mastery", entry.RewardSource);
        Assert.Equal("minor-healing-potion", entry.Code);
    }

    [Fact]
    public async Task MixedMasteriesUseIndependentPoolsAndPreserveExperienceAndFirstClear()
    {
        await using var test = await Scenario.CreateAsync(4, 0);
        await test.Rewards.RecordAsync(test.Room, "slime-field", test.Participants, "monster:1", false);
        await test.Rewards.RecordAsync(test.Room, "slime-field", test.Participants, "clear", true);
        // Same profile under a first-clear event must not receive mastery or challenge bonuses.
        await test.Rewards.RecordAsync(test.Room, "slime-field", test.Participants, "first-clear", true);
        await test.Db.SaveChangesAsync();
        var entries = await test.Db.RewardEntries.ToListAsync();
        Assert.Equal(1, entries.Where(e => e.CharacterId == 1 && e.Kind == "Gold" && e.RewardSource == "Mastery").Sum(e => e.Quantity));
        Assert.DoesNotContain(entries, e => e.CharacterId == 2 && e.RewardSource != "Base");
        Assert.Equal(2, entries.Count(e => e.CharacterId == 1 && e.RewardSource == "Mastery" && e.Kind is "Weapon" or "Consumable"));
        Assert.Equal(18, entries.Where(e => e.CharacterId == 1 && e.Kind == "Experience").Sum(e => e.Quantity));
        Assert.DoesNotContain(entries, e => e.EventKey == "first-clear" && e.RewardSource != "Base");
        Assert.DoesNotContain(entries, e => e.RewardSource == "Challenge");
        Assert.Equal(2, await test.Db.DungeonRunParticipants.CountAsync());
    }

    [Fact]
    public async Task CapturedMasterySurvivesReconnectAndOnlyNextAttemptGetsNewBenefits()
    {
        await using var test = await Scenario.CreateAsync(1);
        await test.Progress.CaptureAsync(test.Room, [1]);
        (await test.Db.CharacterDungeonProgress.FindAsync(1, 1))!.HighestDepth = 4;
        await test.Db.SaveChangesAsync();
        test.Db.ChangeTracker.Clear();
        var room = (await test.Db.Rooms.FindAsync(1))!;
        await test.Rewards.RecordAsync(room, "slime-field", test.Participants, "clear", true);
        await test.Db.SaveChangesAsync();
        Assert.Empty(await test.Db.RewardEntries.Where(e => e.RewardSource == "Mastery").ToListAsync());
        room.RunSequence++;
        await test.Rewards.RecordAsync(room, "slime-field", test.Participants, "clear", true);
        await test.Db.SaveChangesAsync();
        Assert.Equal(4, (await test.Db.DungeonRunParticipants.FindAsync(1, 2, 1))!.MasteryLevel);
        Assert.Equal(2, await test.Db.RewardEntries.CountAsync(e => e.Sequence == 2 && e.RewardSource == "Mastery"));
    }

    [Fact]
    public async Task ChallengeFragmentsAreClearOnlyAndCannotBeMultipliedOrReawarded()
    {
        await using var test = await Scenario.CreateAsync(4);
        test.Room.DepthLevel = 5;
        Assert.True(await test.Rewards.RecordAsync(test.Room, "slime-field", test.Participants, "monster:1", false));
        await test.Db.SaveChangesAsync();
        Assert.Empty(await test.Db.RewardEntries.Where(e => e.RewardSource == "Challenge").ToListAsync());
        Assert.True(await test.Rewards.RecordAsync(test.Room, "slime-field", test.Participants, "clear", true));
        Assert.False(await test.Rewards.RecordAsync(test.Room, "slime-field", test.Participants, "clear", true));
        await test.Rewards.SettleAsync(test.Room, true, DateTime.UtcNow, []);
        await test.Db.SaveChangesAsync();
        Assert.Single(await test.Db.RewardEntries.Where(e => e.RewardSource == "Challenge").ToListAsync());
        var quantity = (await test.Db.CharacterItemStacks.SingleAsync(s => s.ItemCode == "challenge-fragment")).Quantity;
        test.Db.ChangeTracker.Clear();
        var room = (await test.Db.Rooms.FindAsync(1))!;
        Assert.False(await test.Rewards.RecordAsync(room, "slime-field", test.Participants, "clear", true));
        await test.Rewards.SettleAsync(room, true, DateTime.UtcNow, []);
        await test.Db.SaveChangesAsync();
        Assert.Equal(quantity, (await test.Db.CharacterItemStacks.SingleAsync(s => s.ItemCode == "challenge-fragment")).Quantity);
    }

    [Fact]
    public async Task DefeatSettlesOnlyEarnedKillRewardsAndNeverAddsProgressOrClearRewards()
    {
        await using var test = await Scenario.CreateAsync(3);
        test.Room.DepthLevel = 5;
        await test.Rewards.RecordAsync(test.Room, "slime-field", test.Participants, "monster:1", false);
        await test.Rewards.SettleAsync(test.Room, false, DateTime.UtcNow, []);
        await test.Db.SaveChangesAsync();
        Assert.Equal("Defeat", (await test.Db.RewardRuns.SingleAsync()).Status);
        Assert.Equal(2, await test.Db.CharacterWeapons.CountAsync());
        Assert.Empty(await test.Db.RewardEntries.Where(e => e.EventKey == "clear" || e.RewardSource == "Challenge").ToListAsync());
        Assert.Equal(3, (await test.Db.CharacterDungeonProgress.FindAsync(1, 1))!.HighestDepth);
    }

    [Fact]
    public async Task GoldRoundingCarriesAcrossKillsAndDoesNotIncludeFirstClear()
    {
        await using var test = await Scenario.CreateAsync(2);
        for (var monster = 1; monster <= 5; monster++)
        {
            await test.Rewards.RecordAsync(test.Room, "slime-field", test.Participants, $"monster:{monster}", false);
            await test.Db.SaveChangesAsync();
        }
        await test.Rewards.RecordAsync(test.Room, "slime-field", test.Participants, "clear", true);
        await test.Rewards.RecordAsync(test.Room, "slime-field", test.Participants, "first-clear", true);
        await test.Db.SaveChangesAsync();
        Assert.Equal(2, await test.Db.RewardEntries.Where(e => e.Kind == "Gold" && e.RewardSource == "Mastery").SumAsync(e => e.Quantity));
        Assert.Equal(35, await test.Db.RewardEntries.Where(e => e.Kind == "Gold" && e.RewardSource == "Base").SumAsync(e => e.Quantity));
    }

    [Fact]
    public async Task RoomFreezesRewardDefinitionAndCharacterClearEligibilityExcludesNonParticipants()
    {
        await using var test = await Scenario.CreateAsync(0, 0);
        test.Room.DepthDefinitionJson = null;
        var frozen = await test.Progress.DefinitionAsync(test.Room);
        Assert.NotNull(test.Room.DepthDefinitionJson);
        frozen!.GoldBonusPercent = 80;
        Assert.Equal(10, (await test.Progress.DefinitionAsync(test.Room))!.GoldBonusPercent);
        test.Room.DepthLevel = 4;
        var boss = new Monster { Id = 1, Name = "Boss", Hp = 0, MaxHp = 10, Attack = 1 };
        test.Db.Monsters.Add(boss);
        await test.Db.SaveChangesAsync();
        var runs = new DungeonRunService(test.Db, test.Rewards, depthProgress: test.Progress);
        await runs.AdvanceAfterDefeatAsync(test.Room, boss, test.Participants, DateTime.UtcNow, [], [1], [1, 99]);
        await test.Db.SaveChangesAsync();
        Assert.Equal(4, (await test.Db.CharacterDungeonProgress.FindAsync(1, 1))!.HighestDepth);
        Assert.Null(await test.Db.CharacterDungeonProgress.FindAsync(2, 1));
        Assert.Null(await test.Db.CharacterDungeonProgress.FindAsync(99, 1));
        Assert.Empty(await test.Db.RewardEntries.Where(e => e.CharacterId == 2).ToListAsync());
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public GameDbContext Db { get; }
        public Room Room { get; }
        public RewardService Rewards { get; }
        public DungeonDepthProgressService Progress { get; }
        public List<RewardParticipant> Participants { get; }

        private Scenario(SqliteConnection connection, GameDbContext db, Room room, RewardService rewards,
            DungeonDepthProgressService progress, List<RewardParticipant> participants)
            => (_connection, Db, Room, Rewards, Progress, Participants) = (connection, db, room, rewards, progress, participants);

        public static async Task<Scenario> CreateAsync(params int[] masteries)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var definition = new DungeonDepthDefinitionOptions
            {
                MaximumDepth = 10, GoldBonusPercent = 10, KillExtraRollChancePercent = 100,
                ClearExtraRollChancePercent = 100, ChallengeFragmentChancePercent = 100,
                ChallengeFragmentCode = "challenge-fragment", ChallengeFragmentQuantity = 1
            };
            var catalog = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions { Dungeons = new() { ["slime-field"] = definition } }));
            var progress = new DungeonDepthProgressService(db, catalog);
            var room = new Room { Id = 1, DungeonId = 1, DepthLevel = 4, DepthDefinitionJson = JsonSerializer.Serialize(definition) };
            db.Dungeons.Add(new Dungeon { Id = 1, Code = "slime-field", DungeonKind = "Dungeon", MinimumLevel = 1 });
            db.Rooms.Add(room);
            var participants = new List<RewardParticipant>();
            for (var i = 0; i < masteries.Length; i++)
            {
                var character = new Character { Id = i + 1, UserId = i + 1, Name = $"Role{i}", Level = 1, Hp = 100, MaxHp = 100 };
                db.Users.Add(new User { Id = i + 1, UserName = $"User{i}", ActiveCharacterId = character.Id });
                db.Characters.Add(character);
                participants.Add(new RewardParticipant(i + 1, character));
                if (masteries[i] > 0) db.CharacterDungeonProgress.Add(new CharacterDungeonProgress
                { CharacterId = character.Id, DungeonId = 1, HighestDepth = masteries[i] });
            }
            await db.SaveChangesAsync();
            var rewards = new RewardService(db, RewardTestFactory.CreateCatalog(guaranteedWeapon: true), ProgressionTestFactory.Create(), depthProgress: progress);
            return new Scenario(connection, db, room, rewards, progress, participants);
        }

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }

    private sealed class SequenceRandom(params double[] sequence) : Random
    {
        private int _index;
        public override double NextDouble() => sequence[_index++];
    }
}
