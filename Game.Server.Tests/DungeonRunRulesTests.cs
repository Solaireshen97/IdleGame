using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class DungeonRunRulesTests
{
    [Fact]
    public async Task RestartUsesFrozenSkillsStatusesLootAndPartyScalingWhileNewRoomsUseNewRules()
    {
        await using var test = await Scenario.CreateAsync();
        var original = await test.Rules.EnsureAsync(test.Room);
        Assert.False(await test.Db.DungeonRunRuleSnapshots.AsNoTracking().AnyAsync());
        Assert.Equal(37, test.Monster.Hp);
        await test.Db.SaveChangesAsync();
        test.Db.ChangeTracker.Clear();

        var room = await test.Db.Rooms.SingleAsync();
        var monster = await test.Db.Monsters.SingleAsync();
        var changedCombat = test.Combat.ExportOptions();
        changedCombat.Skills.Single(skill => skill.Code == "acid").DamagePowerPercent = 900;
        changedCombat.StatusEffects.Single(status => status.Code == "armor-break").ValuePerStack = -80;
        var liveCombat = new MonsterCombatCatalog(Options.Create(changedCombat));
        var liveRewards = NewRewards(999, includeFirstClear: false);
        var liveScaling = Scaling(500);
        var rules = new DungeonRunRulesService(test.Db, liveCombat, liveRewards, liveScaling, test.Depths);
        var frozen = await rules.EnsureAsync(room);
        Assert.Equal(original.Revision, frozen.Revision);
        var monsters = new MonsterCombatService(test.Db, liveCombat, new Random(1), runRules: rules);
        var intent = await monsters.EnsureIntentAsync(room, monster);
        Assert.Equal("acid", intent.SkillCode);
        Assert.Equal(120, rules.CombatFor(room).ResolveSkill("acid")!.Effects[0].AttackPowerPercent);
        var character = await test.Db.Characters.SingleAsync();
        var slot = await test.Db.RoomSlots.SingleAsync();
        await monsters.ExecuteIntentAsync(room, monster, [new(slot, character)], new Dictionary<int, ElementType>(), []);
        Assert.Equal(988, character.Hp);
        Assert.Equal(-20m, await monsters.Statuses.ModifierAsync(room, "Character", character.Id, "ReductionPercent"));

        var rewardService = new RewardService(test.Db, liveRewards,
            new ProgressionService(Options.Create(new ProgressionOptions { MaximumLevel = 2, ExperienceToNextLevel = [10] })), runRules: rules);
        Assert.True(await rewardService.RecordAsync(room, "slime-field", [new(1, character)], "monster:1:1", false));
        Assert.False(await rewardService.RecordAsync(room, "slime-field", [new(1, character)], "monster:1:1", false));
        Assert.Equal(3, test.Db.RewardEntries.Local.Single(entry => entry.Kind == "Gold").Quantity);
        Assert.False(liveRewards.HasRewardProfile("slime-field-first-clear", true));
        Assert.True(await rewardService.HasRewardProfileAsync(room, "SLIME-FIELD-first-clear", true));
        var party = new PartyScalingService(test.Db, liveScaling, rules);
        Assert.Equal(180, (await party.PercentagesAsync(room))[1]);
        await party.SynchronizeAsync(room, [slot, new RoomSlot { CharacterId = 99 }]);
        Assert.Equal((180, 67), (monster.MaxHp, monster.Hp));

        var fresh = new Room { DungeonId = room.DungeonId, MonsterId = monster.Id, SlotCount = 5 };
        test.Db.Rooms.Add(fresh);
        await test.Db.SaveChangesAsync();
        // A legacy single-enemy reference is also a supported encounter source.
        var current = await rules.EnsureAsync(fresh);
        Assert.NotEqual(frozen.Revision, current.Revision);
        Assert.Equal(500, current.PartyHpPercentages[1]);
        Assert.Equal(999, current.Rewards.Kills["slime-field"].Gold);
        Assert.Equal(900, rules.CombatFor(fresh).ResolveSkill("acid")!.Effects[0].AttackPowerPercent);
    }

    [Fact]
    public async Task RepeatRestoresFrozenEncounterAndPreservesExplicitAbsenceOfDepthRules()
    {
        await using var test = await Scenario.CreateAsync();
        var original = await test.Rules.EnsureAsync(test.Room);
        Assert.Equal(DungeonRewardEligibility.CurrentSlots, original.RewardEligibility);
        await test.Db.SaveChangesAsync();
        var newDepths = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions
        {
            Dungeons = new() { ["slime-field"] = new() { ChallengeFragmentCode = "fragment" } }
        }));
        var newRules = new DungeonRunRulesService(test.Db, test.Combat, NewRewards(99), Scaling(500), newDepths);
        var progress = new DungeonDepthProgressService(test.Db, newDepths, newRules);
        Assert.Null(await progress.DefinitionAsync(test.Room));
        test.Monster.Hp = 0;
        test.Monster.Attack = 777;
        test.Monster.MaxHp = 9999;
        test.Room.RunSequence++;
        var repeated = await newRules.RefreshAsync(test.Room);
        Assert.Equal(original.Revision, repeated.Revision);
        Assert.Equal((100, 100, 10), (test.Monster.Hp, test.Monster.MaxHp, test.Monster.Attack));
        Assert.Single(test.Db.DungeonRunRuleSnapshots.Local);
    }

    [Fact]
    public async Task AbortedCaptureDoesNotLeakThroughScopedCacheAndDeletionCascades()
    {
        await using var test = await Scenario.CreateAsync();
        await test.Rules.EnsureAsync(test.Room);
        test.Db.ChangeTracker.Clear();
        var room = await test.Db.Rooms.SingleAsync();
        await test.Rules.EnsureAsync(room);
        Assert.Single(test.Db.DungeonRunRuleSnapshots.Local);
        await test.Db.SaveChangesAsync();
        test.Db.Rooms.Remove(room);
        await test.Db.SaveChangesAsync();
        Assert.Empty(await test.Db.DungeonRunRuleSnapshots.ToListAsync());
    }

    [Fact]
    public async Task DamagedSnapshotFailsExplicitlyInsteadOfSilentlyUsingNewRules()
    {
        await using var test = await Scenario.CreateAsync();
        await test.Rules.EnsureAsync(test.Room);
        await test.Db.SaveChangesAsync();
        test.Db.DungeonRunRuleSnapshots.Local.Single().DefinitionJson += " ";
        await test.Db.SaveChangesAsync();
        test.Db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Rules.EnsureAsync(test.Room));
    }

    [Fact]
    public async Task MigrationPreservesExistingRoomsAndCanBeRolledBack()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260930010000_AddBattleStatusOwnership");
        db.Rooms.Add(new Room { Id = 1, DepthLevel = 4, RunSequence = 7, RoundNumber = 11, MonsterId = 9 });
        await db.SaveChangesAsync();
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.DungeonRunRuleSnapshots.ToListAsync());
        db.ChangeTracker.Clear();
        var room = await db.Rooms.SingleAsync();
        Assert.Equal((4, 7, 11, 9), (room.DepthLevel, room.RunSequence, room.RoundNumber, room.MonsterId));
        await migrator.MigrateAsync("20260930010000_AddBattleStatusOwnership");
        Assert.Single(await db.Rooms.ToListAsync());
    }

    private static RewardCatalog NewRewards(int gold, bool includeFirstClear = true) => new(Options.Create(new RewardOptions
    {
        MonsterKills = new() { ["slime-field"] = new() { Gold = gold } },
        DungeonClears = includeFirstClear
            ? new() { ["slime-field"] = new() { Gold = gold }, ["slime-field-first-clear"] = new() { Gold = gold } }
            : new() { ["slime-field"] = new() { Gold = gold } }
    }), ConsumableTestFactory.Create(), new WeaponCatalog(Options.Create(new WeaponOptions
    {
        Items = [new() { Code = "test-weapon", Name = "Test", Attack = 1, MaxHp = 1,
            Skills = [new() { Code = "test-critical", Level = 1 }] }],
        Skills = [new() { Code = "test-critical", Name = "Critical", EffectType = WeaponSkillEffectType.CriticalChancePercent, PercentPerLevel = 1 }],
        StarterPacks = new() { ["knight"] = ["test-weapon"] }
    })));

    private static PartyScalingCatalog Scaling(int percent) => new(Options.Create(new PartyScalingOptions
    {
        Profiles = new() { ["fixed"] = [100, 100, 100, 100, 100], ["scaled"] = [100, percent, percent, percent, percent] }
    }));

    private sealed class Scenario : IAsyncDisposable
    {
        public required SqliteConnection Connection { get; init; }
        public required GameDbContext Db { get; init; }
        public required Room Room { get; init; }
        public required Monster Monster { get; init; }
        public required MonsterCombatCatalog Combat { get; init; }
        public required DungeonDepthCatalog Depths { get; init; }
        public required DungeonRunRulesService Rules { get; init; }
        public static async Task<Scenario> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var room = new Room { Id = 1, DungeonId = 1, MonsterId = 1, SlotCount = 5, RoundNumber = 1, Status = RoomStatus.Preparing };
            var monster = new Monster { Id = 1, RoomId = 1, Name = "Boss", Hp = 37, MaxHp = 100, BaseMaxHp = 100,
                Attack = 10, CombatProfileCode = "acid-slime", RewardProfileCode = "slime-field" };
            db.AddRange(new Dungeon { Id = 1, Code = "slime-field", Name = "Test", PartyScalingProfileCode = "scaled" },
                new User { Id = 1, UserName = "test", PasswordHash = "x" },
                new Character { Id = 1, UserId = 1, Name = "Player", Hp = 1000, MaxHp = 1000 }, room, monster,
                new RoomSlot { Id = 1, RoomId = 1, UserId = 1, CharacterId = 1, SlotIndex = 1 });
            await db.SaveChangesAsync();
            var combat = MonsterCombatTestFactory.CreateCatalog();
            var depths = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions()));
            return new() { Connection = connection, Db = db, Room = room, Monster = monster, Combat = combat, Depths = depths,
                Rules = new(db, combat, NewRewards(3), Scaling(180), depths) };
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Connection.DisposeAsync(); }
    }
}
