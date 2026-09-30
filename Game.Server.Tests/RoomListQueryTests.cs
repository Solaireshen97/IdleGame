using System.Data.Common;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class RoomListQueryTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(40)]
    [InlineData(200)]
    public async Task AuthenticatedListHasConstantThreeQueryBudgetAndStableOrder(int roomCount)
    {
        await using var test = await TestContext.CreateAsync();
        for (var id = 1; id <= roomCount; id++) test.AddRoom(id, isPublic: true);
        await test.SaveAndResetAsync();
        var progression = ProgressionTestFactory.Create();
        var service = new RoomService(test.Db,
            new UserService(test.Db, progression, SkillTestFactory.Create()), progression,
            ConsumableTestFactory.Create(), SkillTestFactory.Create(), RewardTestFactory.CreateService(test.Db, progression));
        var rooms = await service.GetRoomsAsync("viewer-token");
        Assert.Equal(roomCount, rooms.Count);
        Assert.Equal(Enumerable.Range(1, roomCount).Reverse(), rooms.Select(room => room.RoomId));
        Assert.Equal(3, test.Counter.Reads); // Session, user, and one complete room-list projection.
        Assert.Empty(test.Db.ChangeTracker.Entries<Room>());
        Assert.Empty(test.Db.ChangeTracker.Entries<Monster>());
        Assert.Empty(test.Db.ChangeTracker.Entries<Dungeon>());
    }

    [Fact]
    public async Task VisibilityParticipationAndPendingOperationsKeepExistingSemantics()
    {
        await using var test = await TestContext.CreateAsync();
        test.AddRoom(1, isPublic: true);
        test.AddRoom(2); // Reserved slot: visible but not yet a participant.
        test.AddRoom(3);
        test.AddRoom(4, isPublic: true, closed: true);
        test.AddRoom(5, ownerId: 1, closed: true);
        test.AddRoom(6, isPublic: true, closed: true); // Closed public room is hidden from outsiders.
        test.AddRoom(7);
        test.AddRoom(8, ownerId: 1, isPublic: true);
        test.Db.RoomSlots.AddRange(
            new RoomSlot { RoomId = 2, SlotIndex = 1, UserId = 1 },
            new RoomSlot { RoomId = 3, SlotIndex = 1, UserId = 1, CharacterId = 101 },
            new RoomSlot { RoomId = 4, SlotIndex = 1, UserId = 1 });
        test.Db.RoomOperations.AddRange(
            new RoomOperation { RoomId = 3, UserId = 1 },
            new RoomOperation { RoomId = 3, UserId = 1 },
            new RoomOperation { RoomId = 3, UserId = 2 },
            new RoomOperation { RoomId = 3, UserId = 1, Status = "Applied" },
            new RoomOperation { RoomId = 2, UserId = 1 });
        await test.SaveAndResetAsync();
        var rooms = await test.Query.ReadAsync(1);
        Assert.Equal(new[] { 8, 3, 2, 1, 5, 4 }, rooms.Select(room => room.RoomId));
        Assert.False(rooms.Single(room => room.RoomId == 2).IsCurrentUserParticipant);
        Assert.True(rooms.Single(room => room.RoomId == 3).IsCurrentUserParticipant);
        Assert.True(rooms.Single(room => room.RoomId == 4).IsCurrentUserParticipant);
        Assert.True(rooms.Single(room => room.RoomId == 5).IsOwnedByCurrentUser);
        Assert.False(rooms.Single(room => room.RoomId == 5).IsCurrentUserParticipant);
        Assert.Equal(2, rooms.Single(room => room.RoomId == 3).PendingOperationCount);
        Assert.Equal(1, rooms.Single(room => room.RoomId == 2).PendingOperationCount);
        Assert.Equal(1, test.Counter.Reads);
        var anonymous = await test.Query.ReadAsync(null);
        Assert.Equal(new[] { 8, 1 }, anonymous.Select(room => room.RoomId));
        Assert.All(anonymous, room =>
        {
            Assert.False(room.IsCurrentUserParticipant);
            Assert.False(room.IsOwnedByCurrentUser);
            Assert.Equal(0, room.PendingOperationCount);
        });
    }

    [Fact]
    public async Task SummaryKeepsDepthNamesWaveCountsAndMissingDungeonFallback()
    {
        await using var test = await TestContext.CreateAsync();
        var room = test.AddRoom(1, isPublic: true);
        room.DepthLevel = 3;
        room.DepthDefinitionJson = "{}";
        room.CurrentWaveNumber = 2;
        room.TotalWaveCount = 4;
        var monster = test.Db.Monsters.Local.Single(item => item.Id == room.MonsterId);
        monster.RoomId = 1;
        monster.WaveNumber = 2;
        monster.Position = 2;
        monster.Hp = 17;
        monster.MaxHp = 90;
        test.Db.Monsters.AddRange(
            new Monster { Id = 10, RoomId = 1, WaveNumber = 2, Position = 3 },
            new Monster { Id = 11, RoomId = 1, WaveNumber = 3, Position = 1 });
        test.AddRoom(2, isPublic: true).DungeonId = 999;
        test.Db.Rooms.Add(new Room { Id = 3, DungeonId = 1, MonsterId = 999, IsPublic = true });
        await test.SaveAndResetAsync();
        var rooms = await test.Query.ReadAsync(null);
        Assert.Equal(2, rooms.Count); // Rooms lacking a monster were already omitted by the old projection.
        var summary = rooms.Single(item => item.RoomId == 1);
        Assert.Equal(test.Depths.DisplayName("Forest", 3), summary.DungeonName);
        Assert.Equal("forest", summary.RegionCode);
        Assert.Equal("Forest region", summary.RegionName);
        Assert.Equal(2, summary.EnemiesInCurrentWave);
        Assert.Equal(2, summary.CurrentEnemyNumber);
        Assert.Equal(2, summary.CurrentWaveNumber);
        Assert.Equal(4, summary.TotalWaveCount);
        Assert.Equal(17, summary.MonsterHp);
        Assert.Equal(90, summary.MonsterMaxHp);
        var missingDungeon = rooms.Single(item => item.RoomId == 2);
        Assert.Equal("", missingDungeon.DungeonName);
        Assert.Equal("", missingDungeon.RegionCode);
        Assert.Equal(1, missingDungeon.EnemiesInCurrentWave);
        Assert.Equal(1, test.Counter.Reads);
    }

    private sealed class TestContext(SqliteConnection connection, GameDbContext db, ReadCounter counter) : IAsyncDisposable
    {
        public GameDbContext Db => db;
        public ReadCounter Counter => counter;
        public DungeonDepthCatalog Depths { get; } = new(Options.Create(new DungeonDepthOptions()));
        public RoomListQuery Query => new(Db, Depths);

        public static async Task<TestContext> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var counter = new ReadCounter();
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>()
                .UseSqlite(connection).AddInterceptors(counter).Options);
            await db.Database.EnsureCreatedAsync();
            db.AddRange(new User { Id = 1, UserName = "viewer", PasswordHash = "unused" },
                new UserLoginSession { UserId = 1, Token = "viewer-token", ExpireAt = DateTime.UtcNow.AddDays(1) },
                new Dungeon { Id = 1, Code = "forest", Name = "Forest", RegionCode = "forest", RegionName = "Forest region" });
            await db.SaveChangesAsync();
            return new(connection, db, counter);
        }

        public Room AddRoom(int id, int ownerId = 2, bool isPublic = false, bool closed = false)
        {
            var room = new Room
            {
                Id = id, DungeonId = 1, MonsterId = id, OwnerUserId = ownerId,
                IsPublic = isPublic, ClosedAtUtc = closed ? DateTime.UtcNow : null
            };
            Db.Rooms.Add(room);
            Db.Monsters.Add(new Monster { Id = id, Name = $"Monster {id}", Hp = 40, MaxHp = 50 });
            return room;
        }

        public async Task SaveAndResetAsync()
        {
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            Counter.Reads = 0;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class ReadCounter : DbCommandInterceptor
    {
        public int Reads { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) Reads++;
            return ValueTask.FromResult(result);
        }
    }
}
