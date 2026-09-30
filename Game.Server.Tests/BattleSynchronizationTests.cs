using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Fact]
    public async Task BattleSynchronizationReturnsLatePublishedHistoryAtSameRoomVersion()
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Room.IsPreparationTimeoutEnabled = false;
        await test.Db.SaveChangesAsync();
        var history = new BattleLogStore();
        history.Append(test.Room.Id, ["first"], DateTime.UtcNow,
            [SynchronizationEvent(test.Room.Id, test.Room.Version)]);
        await using var db = test.CreateDbContext();
        var sync = SynchronizationService(db, history);
        var (initial, initialError) = await sync.SynchronizeAsync(new()
        {
            RoomId = test.Room.Id, HistoryEpoch = history.Epoch
        }, test.Token);
        Assert.Null(initialError);
        Assert.NotNull(initial);
        Assert.NotNull(initial.Room);
        Assert.Single(initial.Room.BattleEvents);
        Assert.Single(initial.Room.BattleLogs);

        history.Append(test.Room.Id, ["published after previous projection"], DateTime.UtcNow,
            [SynchronizationEvent(test.Room.Id, initial.Room.RoomVersion, sequence: 2)]);
        var (late, lateError) = await sync.SynchronizeAsync(new()
        {
            RoomId = test.Room.Id, HistoryEpoch = history.Epoch,
            AfterEventId = initial.LastEventId, AfterLogId = initial.LastLogId
        }, test.Token);

        Assert.Null(lateError);
        Assert.NotNull(late);
        Assert.NotNull(late.Room);
        Assert.False(late.HistoryReset);
        Assert.Equal(initial.Room.RoomVersion, late.Room.RoomVersion);
        Assert.True(Assert.Single(late.Room.BattleEvents).Id > initial.LastEventId);
        Assert.Equal("published after previous projection", Assert.Single(late.Room.BattleLogs).Text);
        var (unchanged, _) = await sync.SynchronizeAsync(new()
        {
            RoomId = test.Room.Id, HistoryEpoch = history.Epoch,
            AfterEventId = late.LastEventId, AfterLogId = late.LastLogId
        }, test.Token);
        Assert.NotNull(unchanged);
        Assert.NotNull(unchanged.Room);
        Assert.Empty(unchanged.Room.BattleEvents);
        Assert.Empty(unchanged.Room.BattleLogs);
        Assert.Equal(late.LastEventId, unchanged.LastEventId);
        Assert.Equal(late.LastLogId, unchanged.LastLogId);
    }

    [Fact]
    public void BattleSynchronizationCursorReturnsOnlyNewEventsAndLogs()
    {
        var room = SynchronizationDetail();
        var response = BattleSynchronizationService.CreateResponse(room, new()
        {
            RoomId = 1, HistoryEpoch = "epoch", AfterEventId = 10, AfterLogId = 100
        });

        Assert.False(response.HistoryReset);
        Assert.NotNull(response.Room);
        Assert.Equal(20, Assert.Single(response.Room.BattleEvents).Id);
        Assert.Equal(110, Assert.Single(response.Room.BattleLogs).Id);
        Assert.Equal(20, response.LastEventId);
        Assert.Equal(110, response.LastLogId);
    }

    [Fact]
    public void BattleSynchronizationChangedProcessEpochResendsRetainedHistory()
    {
        var response = BattleSynchronizationService.CreateResponse(SynchronizationDetail(), new()
        {
            RoomId = 1, HistoryEpoch = "previous-process", AfterEventId = 20, AfterLogId = 110
        });

        Assert.True(response.HistoryReset);
        Assert.NotNull(response.Room);
        Assert.Equal(new long[] { 10, 20 }, response.Room.BattleEvents.Select(item => item.Id));
        Assert.Equal(new long[] { 100, 110 }, response.Room.BattleLogs.Select(item => item.Id));
    }

    [Theory]
    [InlineData(5, 100)]
    [InlineData(10, 50)]
    [InlineData(30, 100)]
    [InlineData(10, 120)]
    public void BattleSynchronizationCursorOutsideRetainedWindowResetsBothHistories(long eventCursor, long logCursor)
    {
        var response = BattleSynchronizationService.CreateResponse(SynchronizationDetail(), new()
        {
            RoomId = 1, HistoryEpoch = "epoch", AfterEventId = eventCursor, AfterLogId = logCursor
        });

        Assert.True(response.HistoryReset);
        Assert.NotNull(response.Room);
        Assert.Equal(2, response.Room.BattleEvents.Count);
        Assert.Equal(2, response.Room.BattleLogs.Count);
    }

    [Fact]
    public void BattleSynchronizationEvictedRoomHistoryResetsAndClearsCursors()
    {
        var history = new BattleLogStore(maximumRooms: 1);
        history.Append(1, ["evicted"], DateTime.UtcNow, [SynchronizationEvent(1, 1)]);
        var old = history.GetSnapshot(1);
        history.Append(2, ["retained"], DateTime.UtcNow, [SynchronizationEvent(2, 1)]);
        var expired = history.GetSnapshot(1);
        var response = BattleSynchronizationService.CreateResponse(new()
        {
            RoomId = 1, BattleHistoryEpoch = expired.Epoch,
            BattleEvents = expired.Events, BattleLogs = expired.Logs
        }, new()
        {
            RoomId = 1, HistoryEpoch = old.Epoch,
            AfterEventId = old.Events.Single().Id, AfterLogId = old.Logs.Single().Id
        });

        Assert.True(response.HistoryReset);
        Assert.NotNull(response.Room);
        Assert.Empty(response.Room.BattleEvents);
        Assert.Empty(response.Room.BattleLogs);
        Assert.Equal(0, response.LastEventId);
        Assert.Equal(0, response.LastLogId);
    }

    [Fact]
    public async Task BattleSynchronizationEnforcesVisibilityAndOwnSkillPrivacy()
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Room.IsPreparationTimeoutEnabled = false;
        test.Character.ProfessionCode = "knight";
        var otherSlot = await test.AddOtherMemberAsync();
        var otherCharacter = await test.Db.Characters.SingleAsync(item => item.Id == otherSlot.CharacterId);
        otherCharacter.ProfessionCode = "knight";
        await test.AddSkillAsync(test.Character, 1, "knight-strike", autoUse: false);
        await test.AddSkillAsync(otherCharacter, 1, "knight-guard", autoUse: false);
        test.Db.AddRange(new User { Id = 3, UserName = "spectator", PasswordHash = "x" },
            new UserLoginSession
            {
                UserId = 3, Token = "spectator-token", CreatedAt = DateTime.UtcNow,
                ExpireAt = DateTime.UtcNow.AddDays(1)
            },
            new Room
            {
                Id = 2, DungeonId = test.Room.DungeonId, MonsterId = test.Monster.Id,
                OwnerUserId = 1, SlotCount = 5, IsPublic = false, IsPreparationTimeoutEnabled = false
            });
        await test.Db.SaveChangesAsync();
        var history = new BattleLogStore();
        history.Append(1, ["room-one"], DateTime.UtcNow, [SynchronizationEvent(1, 0)]);
        history.Append(2, ["private-room-two"], DateTime.UtcNow, [SynchronizationEvent(2, 0)]);

        async Task<(BattleSyncResponse? Response, string? Error)> Read(int roomId, string token)
        {
            await using var db = test.CreateDbContext();
            return await SynchronizationService(db, history).SynchronizeAsync(new() { RoomId = roomId }, token);
        }
        var (owner, ownerError) = await Read(1, test.Token);
        Assert.Null(ownerError);
        Assert.NotNull(owner);
        Assert.NotNull(owner.Room);
        Assert.Equal("knight-strike", Assert.Single(owner.Room.Slots[0].Skills, skill => skill.SkillCode is not null).SkillCode);
        Assert.Empty(owner.Room.Slots[1].Skills);
        Assert.All(owner.Room.BattleEvents, item => Assert.Equal(1, item.RoomId));
        Assert.DoesNotContain(owner.Room.BattleLogs, item => item.Text == "private-room-two");
        var (member, memberError) = await Read(1, "other-token");
        Assert.Null(memberError);
        Assert.NotNull(member);
        Assert.NotNull(member.Room);
        Assert.Empty(member.Room.Slots[0].Skills);
        Assert.Equal("knight-guard", Assert.Single(member.Room.Slots[1].Skills, skill => skill.SkillCode is not null).SkillCode);
        var (spectator, spectatorError) = await Read(1, "spectator-token");
        Assert.Null(spectatorError);
        Assert.NotNull(spectator);
        Assert.NotNull(spectator.Room);
        Assert.All(spectator.Room.Slots, slot => Assert.Empty(slot.Skills));
        var (denied, deniedError) = await Read(2, "spectator-token");
        Assert.Null(denied);
        Assert.Equal("NotFound", deniedError);
        var (privateOwner, privateOwnerError) = await Read(2, test.Token);
        Assert.Null(privateOwnerError);
        Assert.NotNull(privateOwner);
        Assert.NotNull(privateOwner.Room);
        Assert.Equal("private-room-two", Assert.Single(privateOwner.Room.BattleLogs).Text);
        var (missing, missingError) = await Read(999, test.Token);
        Assert.Null(missing);
        Assert.Equal("NotFound", missingError);
    }

    [Fact]
    public async Task BattleSynchronizationRefreshesPresenceWithoutReadingRewardHistory()
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Room.IsPreparationTimeoutEnabled = false;
        var slot = await test.Db.RoomSlots.SingleAsync();
        slot.LastSeenAtUtc = DateTime.UtcNow.AddMinutes(-10);
        test.Db.RewardRuns.Add(new RewardRun { RoomId = test.Room.Id, Sequence = 1 });
        test.Db.RewardEntries.Add(new RewardEntry
        {
            RoomId = test.Room.Id, Sequence = 1, UserId = 1, CharacterId = 1,
            Kind = "Gold", Quantity = 500
        });
        await test.Db.SaveChangesAsync();
        var before = DateTime.UtcNow;
        var commands = new RoomDetailCommandCounter();
        var options = new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite(test.Db.Database.GetConnectionString()!).AddInterceptors(commands).Options;
        await using var db = new GameDbContext(options);
        var (response, error) = await SynchronizationService(db, new BattleLogStore()).SynchronizeAsync(
            new() { RoomId = test.Room.Id }, test.Token);

        Assert.Null(error);
        Assert.NotNull(response);
        Assert.NotNull(response.Room);
        Assert.Null(response.Room.Rewards);
        Assert.Null(response.Room.CumulativeRewards);
        Assert.Equal(0, commands.RewardSelects);
        await using var verification = test.CreateDbContext();
        Assert.True((await verification.RoomSlots.SingleAsync()).LastSeenAtUtc >= before);
        Assert.False(Assert.Single(response.Room.Slots).IsOffline);
    }

    [Fact]
    public async Task BattleSynchronizationStillSettlesFullyPreparedRound()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 20, monsterAttack: 12, monsterDefense: 0);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Room.Status = RoomStatus.Preparing;
        test.Room.PreparationStartedAtUtc = DateTime.UtcNow;
        (await test.Db.RoomSlots.SingleAsync()).IsConfirmed = true;
        await test.Db.SaveChangesAsync();
        var history = new BattleLogStore();
        var commands = new RoomDetailCommandCounter();
        var options = new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite(test.Db.Database.GetConnectionString()!).AddInterceptors(commands).Options;
        await using var db = new GameDbContext(options);
        var (response, error) = await SynchronizationService(db, history).SynchronizeAsync(
            new() { RoomId = test.Room.Id }, test.Token);

        Assert.Null(error);
        Assert.NotNull(response);
        Assert.NotNull(response.Room);
        Assert.Equal(1, response.Room.RoundNumber);
        Assert.Equal(RoomStatus.Cooldown, response.Room.RoomStatus);
        Assert.Equal(980, response.Room.MonsterHp);
        Assert.Equal(88, Assert.Single(response.Room.Slots).CharacterHp);
        Assert.Equal(2, response.Room.BattleEvents.Count(item => item.Kind == BattleEventKind.Damage));
        Assert.NotEmpty(response.Room.BattleLogs);
        Assert.Null(response.Room.Rewards);
        Assert.Null(response.Room.CumulativeRewards);
        Assert.Equal(0, commands.RewardSelects);
        await using var verification = test.CreateDbContext();
        Assert.Equal(1, (await verification.Rooms.SingleAsync()).RoundNumber);
        Assert.False((await verification.RoomSlots.SingleAsync()).IsConfirmed);
    }

    private static BattleSynchronizationService SynchronizationService(GameDbContext db, BattleLogStore history)
    {
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        var users = new UserService(db, progression, skills);
        var rewards = RewardTestFactory.CreateService(db, progression);
        var rooms = new RoomService(db, users, progression, ConsumableTestFactory.Create(), skills, rewards,
            battleLogStore: history);
        var battles = new BattleService(db, users, ConsumableTestFactory.Create(), skills, rewards,
            battleLogStore: history);
        return new(db, battles, rooms);
    }

    private static BattleEventResponse SynchronizationEvent(int roomId, int version, int sequence = 1) => new()
    {
        RoomId = roomId, SettlementVersion = version, Sequence = sequence,
        Kind = BattleEventKind.Damage, ActionKind = BattleActionKind.NormalAttack,
        Target = new("Monster", 1), Source = new("Character", 1), ActualAmount = 20
    };

    private static RoomDetailResponse SynchronizationDetail() => new()
    {
        RoomId = 1, RoomVersion = 9, BattleHistoryEpoch = "epoch",
        BattleEvents = [SynchronizationEvent(1, 9) with { Id = 10 }, SynchronizationEvent(1, 9, 2) with { Id = 20 }],
        BattleLogs = [new() { Id = 100, Text = "old" }, new() { Id = 110, Text = "new" }]
    };
}
