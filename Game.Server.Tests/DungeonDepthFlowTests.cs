using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class DungeonDepthFlowTests
{
    [Fact]
    public async Task CreationValidatesDepthAndAccountUnlocksExactlyOneNextLayerPerClear()
    {
        await using var test = await Scenario.CreateAsync();
        foreach (var depth in new[] { 0, 7 })
            Assert.Equal("InvalidDungeonDepth", (await test.Rooms.CreateRoomAsync(100, null, "owner", depthLevel: depth)).Error);
        Assert.Equal("DungeonDepthLocked", (await test.Rooms.CreateRoomAsync(100, null, "owner", depthLevel: 2)).Error);
        Assert.Empty(await test.Db.Rooms.ToListAsync());

        for (var depth = 1; depth <= 4; depth++)
        {
            var room = await test.CreateRoomAsync(100, depth);
            Assert.Equal(depth, room.DepthLevel);
            await test.FinishAsync(room);
            var preview = (await test.Rooms.GetDungeonAsync(100, "owner", depth))!;
            Assert.Equal(depth + 1, preview.UnlockedDepth);
            Assert.Equal(depth, preview.CharacterHighestDepth);
            Assert.Equal(depth, preview.MasteryLevel);
            Assert.True(preview.AutoUnlocked);
            Assert.Equal(depth, (await test.Db.UserDungeonClears.SingleAsync(clear => clear.UserId == 1 && clear.DungeonId == 100)).HighestDepth);
            Assert.Null((await test.Rooms.RemoveSlotAsync(room.Id, 1, "owner")).Error);
            Assert.False(await test.Db.CharacterActivities.AnyAsync(activity => activity.CharacterId == 1));
        }
    }

    [Fact]
    public async Task CrossAccountJoinAndQueuedJoinBothRequireTheGuestsOwnUnlock()
    {
        await using var test = await Scenario.CreateAsync();
        await test.UnlockAsync(1, 100, 3);
        var room = await test.CreateRoomAsync(100, 4, repeat: true);
        Assert.Equal("DungeonDepthLocked", (await test.Rooms.JoinRoomAsync(room.Id, new JoinRoomRequest { SlotIndex = 2 }, "guest")).Error);
        room.Status = RoomStatus.Cooldown;
        room.RoundNumber = 1;
        room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddMinutes(1);
        await test.Db.SaveChangesAsync();
        Assert.Equal("DungeonDepthLocked", (await test.Rooms.SubmitOperationAsync(room.Id,
            new SubmitRoomOperationRequest { Kind = RoomOperationKind.Join, SlotIndex = 2 }, "guest")).Error);
        Assert.Empty(await test.Db.RoomOperations.ToListAsync());
        Assert.Null((await test.Db.RoomSlots.SingleAsync(slot => slot.RoomId == room.Id && slot.SlotIndex == 2)).CharacterId);
        Assert.False(await test.Db.CharacterActivities.AnyAsync(activity => activity.CharacterId == 2));
    }

    [Fact]
    public async Task SharedAccountUnlockAllowsUnclearedAltToSkipToFourButDoesNotGrantAutoOrMastery()
    {
        await using var test = await Scenario.CreateAsync();
        await test.UnlockAsync(1, 100, 3);
        (await test.Db.Users.FindAsync(1))!.ActiveCharacterId = 3;
        await test.Db.SaveChangesAsync();
        var before = (await test.Rooms.GetDungeonAsync(100, "owner", 4))!;
        Assert.True(before.CanEnter);
        Assert.Equal(4, before.UnlockedDepth);
        Assert.Equal(0, before.MasteryLevel);
        Assert.Equal(0, before.CharacterHighestDepth);
        Assert.False(before.AutoUnlocked);
        var room = await test.CreateRoomAsync(100, 4);
        await test.FinishAsync(room);
        var after = (await test.Rooms.GetDungeonAsync(100, "owner", 4))!;
        Assert.Equal(4, after.MasteryLevel);
        Assert.Equal(4, after.CharacterHighestDepth);
        Assert.Equal(5, after.UnlockedDepth);
        Assert.True(after.AutoUnlocked);
        Assert.Equal(10m, after.GoldBonusPercent);
        Assert.Equal(10m, after.KillExtraRollChancePercent);
        Assert.Equal(10m, after.ClearExtraRollChancePercent);
        Assert.Equal(0, (await test.Db.DungeonRunParticipants.SingleAsync(item => item.RoomId == room.Id && item.CharacterId == 3)).MasteryLevel);
        Assert.False(await test.Db.CharacterDungeonProgress.AnyAsync(item => item.CharacterId == 1));
        Assert.Contains(test.Logs, log => log.Contains("精通提升至 LV4") && log.Contains("下轮生效"));
    }

    [Theory]
    [InlineData(100, 1)]
    [InlineData(101, 2)]
    public async Task RealBattleCompletesHigherLayerAndRepeatPreservesScaledHealthAndConfiguredStage(int dungeonId, int stage)
    {
        await using var test = await Scenario.CreateAsync();
        await test.UnlockAsync(1, dungeonId, 4);
        var room = await test.CreateRoomAsync(dungeonId, 5, repeat: true);
        var preview = (await test.Rooms.GetDungeonAsync(dungeonId, "owner", 5))!;
        Assert.Equal(stage, preview.Stage);
        Assert.Equal(2, preview.WaveCount);
        Assert.Equal(new[] { 147, 293 }, preview.Monsters.Select(monster => monster.MaxHp));
        Assert.Equal(new[] { 2, 3 }, preview.Monsters.Select(monster => monster.Attack));
        Assert.All(preview.Monsters, monster => Assert.Equal(0, monster.Defense));
        Assert.Equal(3, preview.Depths.Single(depth => depth.DepthLevel == 5).AddedMechanics.Count);
        var monsters = await test.Db.Monsters.Where(monster => monster.RoomId == room.Id).OrderBy(monster => monster.WaveNumber).ToListAsync();
        Assert.Equal(new[] { 147, 293 }, monsters.Select(monster => monster.BaseMaxHp));
        await test.FinishAsync(room);
        Assert.Equal(5, (await test.Db.CharacterDungeonProgress.SingleAsync(item => item.CharacterId == 1 && item.DungeonId == dungeonId)).HighestDepth);
        Assert.Equal(4, (await test.Rooms.GetDungeonAsync(dungeonId, "owner"))!.MasteryLevel);
        var firstCount = await test.Db.RewardEvents.CountAsync(item => item.RoomId == room.Id && item.Sequence == 1);
        Assert.Equal(3, firstCount);
        Assert.All(monsters, monster => Assert.Equal(0, monster.Hp));
        var entriesBeforeReconnect = await test.Db.RewardEntries.CountAsync(item => item.RoomId == room.Id);
        Assert.Null((await test.Battle.SyncRoomAsync(room.Id)).Error);
        Assert.Null((await test.Battle.SyncRoomAsync(room.Id)).Error);
        Assert.Equal(1, room.RunSequence);
        Assert.Equal(entriesBeforeReconnect, await test.Db.RewardEntries.CountAsync(item => item.RoomId == room.Id));
        Assert.Equal(3, await test.Db.RewardEvents.CountAsync(item => item.RoomId == room.Id && item.Sequence == 1));

        room.BattleEndedAtUtc = DateTime.UtcNow.AddSeconds(-BattleRules.RepeatBattleDelaySeconds - 1);
        await test.Db.SaveChangesAsync();
        Assert.Null((await test.Battle.SyncRoomAsync(room.Id)).Error);
        Assert.Equal(2, room.RunSequence);
        Assert.Equal(5, room.DepthLevel);
        Assert.Equal(new[] { 147, 293 }, monsters.Select(monster => monster.MaxHp));
        Assert.Equal(new[] { 147, 293 }, monsters.Select(monster => monster.Hp));
        await test.FinishAsync(room);
        Assert.Equal(3, await test.Db.RewardEvents.CountAsync(item => item.RoomId == room.Id && item.Sequence == 2));
        Assert.Equal(4, (await test.Db.DungeonRunParticipants.SingleAsync(item => item.RoomId == room.Id && item.RunSequence == 2 && item.CharacterId == 1)).MasteryLevel);
        Assert.Equal(5, (await test.Db.UserDungeonClears.SingleAsync(item => item.UserId == 1 && item.DungeonId == dungeonId)).HighestDepth);
    }

    [Fact]
    public async Task OrdinaryDungeonOnlyAcceptsLevelOneAndNeverCreatesDepthMastery()
    {
        await using var test = await Scenario.CreateAsync();
        Assert.Equal("InvalidDungeonDepth", (await test.Rooms.CreateRoomAsync(102, null, "owner", depthLevel: 2)).Error);
        var room = await test.CreateRoomAsync(102, 1);
        Assert.Null(room.DepthDefinitionJson);
        Assert.False((await test.Rooms.GetDungeonAsync(102, "owner"))!.SupportsDepths);
        await test.FinishAsync(room);
        Assert.Empty(await test.Db.CharacterDungeonProgress.ToListAsync());
        Assert.Empty(await test.Db.DungeonRunParticipants.ToListAsync());
        Assert.False((await test.Rooms.GetRoomDetailAsync(room.Id, "owner"))!.SupportsDepths);
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly string _path;
        private Scenario(string path, GameDbContext db, RoomService rooms, BattleService battle)
        { _path = path; Db = db; Rooms = rooms; Battle = battle; }
        public GameDbContext Db { get; }
        public RoomService Rooms { get; }
        public BattleService Battle { get; }
        public List<string> Logs { get; } = [];

        public static async Task<Scenario> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"idle-depth-flow-{Guid.NewGuid():N}.db");
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
            await db.Database.EnsureCreatedAsync();
            db.AddRange(
                new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 },
                new User { Id = 2, UserName = "guest", PasswordHash = "x", ActiveCharacterId = 2 },
                new Character { Id = 1, UserId = 1, Name = "Owner", ProfessionCode = "knight", Level = 20, Attack = 10000, Hp = 1000, MaxHp = 1000 },
                new Character { Id = 2, UserId = 2, Name = "Guest", ProfessionCode = "knight", Level = 20, Attack = 10000, Hp = 1000, MaxHp = 1000 },
                new Character { Id = 3, UserId = 1, Name = "Alt", ProfessionCode = "knight", Level = 20, Attack = 10000, Hp = 1000, MaxHp = 1000 },
                new UserLoginSession { UserId = 1, Token = "owner", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) },
                new UserLoginSession { UserId = 2, Token = "guest", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            var definitions = new DungeonDepthOptions();
            var encounters = new DungeonEncounterOptions();
            for (var id = 100; id <= 102; id++)
            {
                var code = $"test-content-{id}";
                db.Dungeons.Add(new Dungeon { Id = id, Code = code, Name = $"副本{id}", DungeonKind = "Dungeon", IsVisible = true,
                    MinimumLevel = id == 101 ? 20 : 10, RecommendedLevel = 20, SlotCount = 5, PartyScalingProfileCode = "fixed", MonsterName = "Test", MonsterMaxHp = 10, MonsterAttack = 1 });
                if (id == 102) continue;
                definitions.Dungeons[code] = new DungeonDepthDefinitionOptions { Stage = id == 101 ? 2 : 1, MaximumDepth = 6,
                    ChallengeFragmentCode = $"t{id - 99}-universal-breakthrough-fragment", ChallengeFragmentChancePercent = 0 };
                encounters.Dungeons[code] = [
                    new DungeonWaveOptions { Monsters = [new EncounterMonsterOptions { Name = "Guard", MaxHp = 100, Attack = 1, RewardProfileCode = "slime-field" }] },
                    new DungeonWaveOptions { Monsters = [new EncounterMonsterOptions { Name = "Boss", MaxHp = 200, Attack = 2, IsBoss = true, RewardProfileCode = "slime-field" }] }];
            }
            await db.SaveChangesAsync();
            var depths = new DungeonDepthCatalog(Options.Create(definitions));
            var progress = new DungeonDepthProgressService(db, depths);
            var progression = ProgressionTestFactory.Create();
            var skills = SkillTestFactory.Create();
            var consumables = ConsumableTestFactory.Create();
            var users = new UserService(db, progression, skills);
            var rewardCatalog = RewardTestFactory.CreateCatalog();
            var rewards = new RewardService(db, rewardCatalog, progression, depthProgress: progress);
            var encounterCatalog = new DungeonEncounterCatalog(Options.Create(encounters), rewardCatalog: rewardCatalog, depthCatalog: depths);
            var rooms = new RoomService(db, users, progression, consumables, skills, rewards, encounterCatalog: encounterCatalog, depthCatalog: depths, depthProgress: progress);
            var runs = new DungeonRunService(db, rewards, depthProgress: progress);
            var battle = new BattleService(db, users, consumables, skills, rewards, dungeonRunService: runs, roomService: rooms);
            return new(path, db, rooms, battle);
        }

        public async Task UnlockAsync(int userId, int dungeonId, int highest)
        {
            Db.UserDungeonClears.Add(new UserDungeonClear { UserId = userId, DungeonId = dungeonId, HighestDepth = highest, ClearedAtUtc = DateTime.UtcNow });
            await Db.SaveChangesAsync();
        }

        public async Task<Room> CreateRoomAsync(int dungeonId, int depth, bool repeat = false)
        {
            var (detail, error) = await Rooms.CreateRoomAsync(dungeonId, null, "owner", isRepeatBattle: repeat,
                isPreparationTimeoutEnabled: false, isPublic: true, depthLevel: depth);
            Assert.Null(error);
            Assert.NotNull(detail);
            return (await Db.Rooms.FindAsync(detail.RoomId))!;
        }

        public async Task FinishAsync(Room room)
        {
            for (var iteration = 0; iteration < 20 && room.Status != RoomStatus.BattleOver; iteration++)
            {
                if (room.Status is RoomStatus.WaveTransition or RoomStatus.Cooldown)
                {
                    room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
                    await Db.SaveChangesAsync();
                    var synced = await Battle.SyncRoomAsync(room.Id);
                    Assert.Null(synced.Error);
                    if (synced.Result is not null) Logs.AddRange(synced.Result.Logs);
                }
                else
                {
                    var fought = await Battle.StartPreparationAsync(room.Id, "owner");
                    Assert.Null(fought.Error);
                    Assert.NotNull(fought.Result);
                    Logs.AddRange(fought.Result.Logs);
                }
            }
            Assert.Equal(RoomStatus.BattleOver, room.Status);
            Assert.All(await Db.Monsters.Where(monster => monster.RoomId == room.Id).ToListAsync(), monster => Assert.Equal(0, monster.Hp));
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            File.Delete(_path);
        }
    }
}
