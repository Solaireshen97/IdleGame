using System.Data.Common;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Fact]
    public async Task ProjectionCacheRepeatedSynchronizationSkipsLargeProjectionTablesButRefreshesPresence()
    {
        await using var test = await BattleTestContext.CreateAsync();
        await PrepareProjectionFixtureAsync(test);
        var revision = new RoomProjectionRevision();
        using var cache = new RoomProjectionCache(revision);
        var history = new BattleLogStore();
        var initialQueries = new ProjectionQueryCounter();
        var initial = await ReadCachedSyncAsync(test, revision, cache, history, new() { RoomId = 1 }, initialQueries);
        Assert.NotNull(initial.Room);
        Assert.NotEmpty(initial.ProjectionId);
        Assert.True(initialQueries.LargeProjectionSelects > 0);
        var initialRevision = revision.Value;
        var before = DateTime.UtcNow;
        var hitQueries = new ProjectionQueryCounter();
        var hit = await ReadCachedSyncAsync(test, revision, cache, history, Cursor(initial), hitQueries);

        Assert.Null(hit.Room);
        Assert.NotNull(hit.Unchanged);
        Assert.Equal(initial.ProjectionId, hit.ProjectionId);
        Assert.Equal(initial.Room.RoomVersion, hit.Unchanged.RoomVersion);
        Assert.True(hit.Unchanged.ServerTimeUtc >= before);
        Assert.Equal(initialRevision, revision.Value);
        Assert.Equal(0, hitQueries.LargeProjectionSelects);
        Assert.True(hitQueries.Selects > 0); // Authorization, visibility and presence remain authoritative.
        Assert.True(hitQueries.Selects < initialQueries.Selects);
        await using var verification = test.CreateDbContext();
        Assert.True((await verification.RoomSlots.SingleAsync()).LastSeenAtUtc >= before);
        _queryOutput.WriteLine($"WholeSynchronize cacheMissSELECT={initialQueries.Selects}, cacheHitSELECT={hitQueries.Selects}, " +
            $"cacheMissLargeProjectionSELECT={initialQueries.LargeProjectionSelects}, cacheHitLargeProjectionSELECT={hitQueries.LargeProjectionSelects}");
    }

    [Fact]
    public async Task ProjectionCacheSameVersionLateHistoryArrivesInUnchangedResponse()
    {
        await using var test = await BattleTestContext.CreateAsync();
        await PrepareProjectionFixtureAsync(test);
        var revision = new RoomProjectionRevision();
        using var cache = new RoomProjectionCache(revision);
        var history = new BattleLogStore();
        history.Append(1, ["first"], DateTime.UtcNow, [SynchronizationEvent(1, test.Room.Version)]);
        var first = await ReadCachedSyncAsync(test, revision, cache, history, new() { RoomId = 1 });
        var cursor = Cursor(first);
        history.Append(1, ["late publication"], DateTime.UtcNow,
            [SynchronizationEvent(1, first.Room!.RoomVersion, sequence: 2)]);
        var queries = new ProjectionQueryCounter();
        var late = await ReadCachedSyncAsync(test, revision, cache, history, cursor, queries);

        Assert.Null(late.Room);
        Assert.False(late.HistoryReset);
        Assert.Equal(first.ProjectionId, late.ProjectionId);
        Assert.Equal(first.Room.RoomVersion, late.Unchanged!.RoomVersion);
        Assert.True(Assert.Single(late.Unchanged.BattleEvents).Id > first.LastEventId);
        Assert.Equal("late publication", Assert.Single(late.Unchanged.BattleLogs).Text);
        Assert.Equal(0, queries.LargeProjectionSelects);
        var stable = await ReadCachedSyncAsync(test, revision, cache, history, Cursor(late));
        Assert.Empty(stable.Unchanged!.BattleEvents);
        Assert.Empty(stable.Unchanged.BattleLogs);
        Assert.Equal(late.LastEventId, stable.LastEventId);
        Assert.Equal(late.LastLogId, stable.LastLogId);
    }

    [Fact]
    public async Task ProjectionCacheDoesNotShareOwnSkillsBetweenDifferentUsers()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var otherSlot = await test.AddOtherMemberAsync();
        var other = await test.Db.Characters.SingleAsync(character => character.Id == otherSlot.CharacterId);
        other.ProfessionCode = "knight";
        await test.AddSkillAsync(other, 1, "knight-guard", autoUse: false);
        await PrepareProjectionFixtureAsync(test);
        var revision = new RoomProjectionRevision();
        using var cache = new RoomProjectionCache(revision);
        var history = new BattleLogStore();
        var owner = await ReadCachedSyncAsync(test, revision, cache, history, new() { RoomId = 1 });
        var member = await ReadCachedSyncAsync(test, revision, cache, history, Cursor(owner), token: "other-token");

        Assert.NotNull(member.Room);
        Assert.NotEqual(owner.ProjectionId, member.ProjectionId);
        Assert.Equal("knight-strike", Assert.Single(owner.Room!.Slots[0].Skills, skill => skill.SkillCode is not null).SkillCode);
        Assert.Empty(owner.Room.Slots[1].Skills);
        Assert.Empty(member.Room.Slots[0].Skills);
        Assert.Equal("knight-guard", Assert.Single(member.Room.Slots[1].Skills, skill => skill.SkillCode is not null).SkillCode);
    }

    [Fact]
    public async Task ProjectionCacheActiveCharacterChangeSelectsDifferentProjectionEvenWithoutInvalidation()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var second = await test.AddSlotAsync(2, "Second");
        await PrepareProjectionFixtureAsync(test);
        var revision = new RoomProjectionRevision();
        using var cache = new RoomProjectionCache(revision);
        var history = new BattleLogStore();
        var first = await ReadCachedSyncAsync(test, revision, cache, history, new() { RoomId = 1 });
        // Intentionally bypass the invalidation interceptor to independently test the key's character dimension.
        (await test.Db.Users.SingleAsync()).ActiveCharacterId = second.Id;
        await test.Db.SaveChangesAsync();
        var secondProjection = await ReadCachedSyncAsync(test, revision, cache, history, Cursor(first));

        Assert.NotNull(secondProjection.Room);
        Assert.NotEqual(first.ProjectionId, secondProjection.ProjectionId);
        Assert.Equal(first.Room!.RoomVersion, secondProjection.Room.RoomVersion);
        Assert.Equal(0, revision.Value);
    }

    [Fact]
    public async Task ProjectionCacheAlwaysRechecksVisibilityAndAuthenticationOnHits()
    {
        await using var test = await BattleTestContext.CreateAsync();
        await PrepareProjectionFixtureAsync(test);
        await test.AddOtherMemberAsync();
        var revision = new RoomProjectionRevision();
        using var cache = new RoomProjectionCache(revision);
        var history = new BattleLogStore();
        // User 2 is a participant while the projection is built, then loses access.
        var member = await ReadCachedSyncAsync(test, revision, cache, history, new() { RoomId = 1 }, token: "other-token");
        test.Db.RoomSlots.Remove(await test.Db.RoomSlots.SingleAsync(slot => slot.UserId == 2));
        test.Room.IsPublic = false;
        await test.Db.SaveChangesAsync(); // No interceptor: fresh authorization must itself prevent cache leakage.
        await using (var db = ProjectionDb(test, revision))
        {
            var (denied, error) = await ProjectionSync(db, history, cache).SynchronizeAsync(Cursor(member), "other-token");
            Assert.Null(denied);
            Assert.Equal("NotFound", error);
        }
        var owner = await ReadCachedSyncAsync(test, revision, cache, history, new() { RoomId = 1 });
        (await test.Db.UserLoginSessions.SingleAsync(session => session.Token == test.Token)).ExpireAt = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        await using (var db = ProjectionDb(test, revision))
        {
            var (denied, error) = await ProjectionSync(db, history, cache).SynchronizeAsync(Cursor(owner), test.Token);
            Assert.Null(denied);
            Assert.Equal("Unauthorized", error);
        }
    }

    [Fact]
    public async Task ProjectionCacheExternalInventorySaveInvalidatesWithoutRoomVersionChange()
    {
        await using var test = await BattleTestContext.CreateAsync();
        await PrepareProjectionFixtureAsync(test);
        var revision = new RoomProjectionRevision();
        using var cache = new RoomProjectionCache(revision);
        var history = new BattleLogStore();
        var first = await ReadCachedSyncAsync(test, revision, cache, history, new() { RoomId = 1 });
        Assert.Equal(3, PotionQuantity(first.Room!));
        await using (var writer = ProjectionDb(test, revision))
        {
            (await writer.CharacterItemStacks.SingleAsync()).Quantity = 9;
            await writer.SaveChangesAsync();
        }
        var refreshed = await ReadCachedSyncAsync(test, revision, cache, history, Cursor(first));

        Assert.NotNull(refreshed.Room);
        Assert.NotEqual(first.ProjectionId, refreshed.ProjectionId);
        Assert.Equal(first.Room!.RoomVersion, refreshed.Room.RoomVersion);
        Assert.Equal(9, PotionQuantity(refreshed.Room));
        Assert.True(revision.Value > 0);
    }

    [Fact]
    public async Task ProjectionCacheExplicitCommitInvalidatesPrecommitReaderAndNeverStoresWriterView()
    {
        await using var test = await BattleTestContext.CreateAsync();
        await PrepareProjectionFixtureAsync(test);
        var revision = new RoomProjectionRevision();
        using var cache = new RoomProjectionCache(revision);
        var history = new BattleLogStore();
        var (first, _) = await ReadCachedProjectionAsync(test, revision, cache, history);
        Assert.Equal(3, PotionQuantity(first!));
        await using var writer = ProjectionDb(test, revision);
        await using var transaction = await writer.Database.BeginTransactionAsync();
        (await writer.CharacterItemStacks.SingleAsync()).Quantity = 9;
        await writer.SaveChangesAsync();
        var revisionAfterSave = revision.Value;
        var (uncommitted, writerProjectionId) = await ProjectionRooms(writer, history)
            .GetSynchronizedRoomAsync(1, test.Token, cache);
        Assert.Equal(9, PotionQuantity(uncommitted!));
        Assert.Empty(writerProjectionId);
        var (precommit, precommitId) = await ReadCachedProjectionAsync(test, revision, cache, history);
        Assert.Equal(3, PotionQuantity(precommit!));
        Assert.NotEmpty(precommitId);
        await transaction.CommitAsync();
        Assert.True(revision.Value > revisionAfterSave);
        var (committed, committedId) = await ReadCachedProjectionAsync(test, revision, cache, history);

        Assert.NotEqual(precommitId, committedId);
        Assert.Equal(first!.RoomVersion, committed!.RoomVersion);
        Assert.Equal(9, PotionQuantity(committed));
    }

    [Fact]
    public async Task ProjectionCachePresenceOnlyWritePreservesRevisionButOfflineBoundaryRebuilds()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var otherSlot = await test.AddOtherMemberAsync();
        otherSlot.LastSeenAtUtc = DateTime.UtcNow;
        await PrepareProjectionFixtureAsync(test);
        var revision = new RoomProjectionRevision();
        using var cache = new RoomProjectionCache(revision);
        var history = new BattleLogStore();
        var first = await ReadCachedSyncAsync(test, revision, cache, history, new() { RoomId = 1 });
        Assert.False(first.Room!.Slots[1].IsOffline);
        var before = revision.Value;
        await using (var writer = ProjectionDb(test, revision))
        {
            (await writer.RoomSlots.SingleAsync(slot => slot.UserId == 2)).LastSeenAtUtc = DateTime.UtcNow.AddMinutes(-2);
            await writer.SaveChangesAsync();
        }
        Assert.Equal(before, revision.Value);
        var refreshed = await ReadCachedSyncAsync(test, revision, cache, history, Cursor(first));

        Assert.NotNull(refreshed.Room);
        Assert.NotEqual(first.ProjectionId, refreshed.ProjectionId);
        Assert.Equal(first.Room.RoomVersion, refreshed.Room.RoomVersion);
        Assert.True(refreshed.Room.Slots[1].IsOffline);
    }

    [Fact]
    public async Task ProjectionCacheRevisionChangedWhileBuildingCannotPublishReusableProjectionId()
    {
        await using var test = await BattleTestContext.CreateAsync();
        await PrepareProjectionFixtureAsync(test);
        var revision = new RoomProjectionRevision();
        using var cache = new RoomProjectionCache(revision);
        var history = new BattleLogStore();
        var bump = new ChangeRevisionDuringProjection(revision);
        await using (var db = ProjectionDb(test, revision, additional: bump))
        {
            var (response, error) = await ProjectionSync(db, history, cache).SynchronizeAsync(new() { RoomId = 1 }, test.Token);
            Assert.Null(error);
            Assert.True(bump.Triggered);
            Assert.NotNull(response!.Room);
            Assert.Empty(response.ProjectionId);
        }
        var queries = new ProjectionQueryCounter();
        var rebuilt = await ReadCachedSyncAsync(test, revision, cache, history, new() { RoomId = 1 }, queries);
        Assert.NotEmpty(rebuilt.ProjectionId);
        Assert.True(queries.LargeProjectionSelects > 0);
        var hit = await ReadCachedSyncAsync(test, revision, cache, history, Cursor(rebuilt));
        Assert.Null(hit.Room);
        Assert.NotNull(hit.Unchanged);
    }

    private static async Task PrepareProjectionFixtureAsync(BattleTestContext test)
    {
        test.Room.IsPreparationTimeoutEnabled = false;
        test.Character.ProfessionCode = "knight";
        await test.AddSkillAsync(test.Character, 1, "knight-strike", autoUse: false);
        await test.AddPotionAsync(test.Character, 3, autoUse: false);
        test.Db.MonsterIntents.Add(new MonsterIntent
        {
            RoomId = test.Room.Id, RunSequence = test.Room.RunSequence, RoundNumber = test.Room.RoundNumber,
            MonsterId = test.Monster.Id, TargetCharacterId = test.Character.Id, TargetType = "Front", CreatedAtUtc = DateTime.UtcNow
        });
        await test.Db.SaveChangesAsync();
    }

    private static GameDbContext ProjectionDb(BattleTestContext test, RoomProjectionRevision revision,
        ProjectionQueryCounter? queries = null, IInterceptor? additional = null)
    {
        var invalidation = new RoomProjectionInvalidation(revision);
        var options = new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite(test.Db.Database.GetConnectionString()!)
            .AddInterceptors(invalidation, new RoomProjectionTransactionInvalidation(invalidation));
        if (queries is not null) options.AddInterceptors(queries);
        if (additional is not null) options.AddInterceptors(additional);
        return new(options.Options);
    }

    private static RoomService ProjectionRooms(GameDbContext db, BattleLogStore history)
    {
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        var users = new UserService(db, progression, skills);
        var combat = new MonsterCombatService(db, new MonsterCombatCatalog(Options.Create(new MonsterCombatOptions())), characterSkills: skills);
        return new(db, users, progression, ConsumableTestFactory.Create(), skills,
            RewardTestFactory.CreateService(db, progression), monsterCombatService: combat, battleLogStore: history);
    }

    private static BattleSynchronizationService ProjectionSync(GameDbContext db, BattleLogStore history, RoomProjectionCache cache) =>
        new(db, EventService(db, history), ProjectionRooms(db, history), cache);

    private static async Task<BattleSyncResponse> ReadCachedSyncAsync(BattleTestContext test, RoomProjectionRevision revision,
        RoomProjectionCache cache, BattleLogStore history, BattleSyncRequest request,
        ProjectionQueryCounter? queries = null, string? token = null)
    {
        await using var db = ProjectionDb(test, revision, queries);
        var (response, error) = await ProjectionSync(db, history, cache).SynchronizeAsync(request, token ?? test.Token);
        Assert.Null(error);
        Assert.NotNull(response);
        return response;
    }

    private static async Task<(RoomDetailResponse? Room, string ProjectionId)> ReadCachedProjectionAsync(
        BattleTestContext test, RoomProjectionRevision revision, RoomProjectionCache cache, BattleLogStore history)
    {
        await using var db = ProjectionDb(test, revision);
        return await ProjectionRooms(db, history).GetSynchronizedRoomAsync(1, test.Token, cache);
    }

    private static BattleSyncRequest Cursor(BattleSyncResponse response) => new()
    {
        RoomId = response.Room?.RoomId ?? response.Unchanged!.RoomId,
        HistoryEpoch = response.Room?.BattleHistoryEpoch ?? response.Unchanged!.BattleHistoryEpoch,
        AfterEventId = response.LastEventId, AfterLogId = response.LastLogId, ProjectionId = response.ProjectionId
    };

    private static int PotionQuantity(RoomDetailResponse room) => Assert.Single(room.Slots[0].Consumables,
        slot => slot.ItemCode == "minor-healing-potion").Quantity;

    private sealed class ProjectionQueryCounter : DbCommandInterceptor
    {
        public int Selects { get; private set; }
        public int LargeProjectionSelects { get; private set; }
        private static readonly string[] LargeTables =
        ["BattleStatusEffects", "CharacterSkillSlots", "CharacterItemStacks", "CharacterWeapons", "CharacterCombatProfessions",
            "BattleConsumableBuffs", "CharacterConsumableSlots", "BattleSkillCooldowns", "BattleConsumableCooldowns"];
        private void Record(DbCommand command)
        {
            if (!command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) return;
            Selects++;
            if (LargeTables.Any(table => command.CommandText.Contains(table, StringComparison.OrdinalIgnoreCase))) LargeProjectionSelects++;
        }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result) { Record(command); return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Record(command); return ValueTask.FromResult(result); }
    }

    private sealed class ChangeRevisionDuringProjection(RoomProjectionRevision revision) : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Triggered && command.CommandText.Contains("CharacterSkillSlots", StringComparison.OrdinalIgnoreCase))
            {
                Triggered = true;
                revision.Changed();
            }
            return ValueTask.FromResult(result);
        }
    }
}
