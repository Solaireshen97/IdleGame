using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Game.Server.Tests;

public sealed class AutoBattleFlowTests
{
    [Fact]
    public async Task BackgroundAutoRoundUsesEnabledSkillAndPublishesRoomLogs()
    {
        await using var test = await AutoBattleTestContext.CreateAsync(isAutoEnabled: true);

        var (result, error) = await test.BattleService.SyncRoomAsync(1);
        var detail = await test.RoomService.GetRoomDetailAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(1, test.Room.RoundNumber);
        Assert.Contains(result!.Logs, log => log.Contains("使用 盾击"));
        Assert.Contains(detail!.BattleLogs, log => log.Text.Contains("自动战斗，本回合自动开始"));
        Assert.Contains(detail.BattleLogs, log => log.Text.Contains("使用 盾击"));
        Assert.Single(await test.Db.BattleSkillCooldowns.ToListAsync());
    }

    [Fact]
    public async Task EnablingLastAutoMemberImmediatelyExecutesTheSameRoundFlow()
    {
        await using var test = await AutoBattleTestContext.CreateAsync(isAutoEnabled: false);

        var (result, error) = await test.BattleService.SetSlotAutoAsync(1,
            new SetSlotAutoRequest { SlotIndex = 1, IsAutoEnabled = true }, test.Token);

        Assert.Null(error);
        Assert.Equal(1, test.Room.RoundNumber);
        Assert.Equal(RoomStatus.Cooldown, result!.RoomStatus);
        Assert.Contains(result.Logs, log => log.Contains("使用 盾击"));
        Assert.Contains(test.LogStore.Get(1), log => log.Text.Contains("使用 盾击"));
    }

    [Fact]
    public async Task HostedRoomCycleAdvancesTwoOfflinePlayersWithoutBrowserRequests()
    {
        await using var test = await AutoBattleTestContext.CreateAsync(isAutoEnabled: false);
        test.Room.StartedAtUtc = DateTime.UtcNow.AddMinutes(-2);
        test.Room.IsPreparationTimeoutEnabled = false;
        var ownerSlot = await test.Db.RoomSlots.SingleAsync(slot => slot.RoomId == 1 && slot.SlotIndex == 1);
        ownerSlot.LastSeenAtUtc = DateTime.UtcNow.AddMinutes(-2);
        test.Db.AddRange(
            new User { Id = 2, UserName = "guest", PasswordHash = "x", ActiveCharacterId = 2 },
            new Character { Id = 2, UserId = 2, Name = "Guest", Hp = 100, MaxHp = 100, Attack = 10 },
            new RoomSlot { RoomId = 1, SlotIndex = 2, UserId = 2, CharacterId = 2,
                LastSeenAtUtc = DateTime.UtcNow.AddMinutes(-2) });
        await test.Db.SaveChangesAsync();

        var connectionString = test.Db.Database.GetConnectionString()!;
        var services = new ServiceCollection();
        services.AddDbContext<GameDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped(provider =>
        {
            var db = provider.GetRequiredService<GameDbContext>();
            var progression = ProgressionTestFactory.Create();
            var skills = SkillTestFactory.Create();
            return new BattleService(db, new UserService(db, progression, skills),
                ConsumableTestFactory.Create(), skills, RewardTestFactory.CreateService(db, progression),
                battleLogStore: test.LogStore);
        });
        await using var provider = services.BuildServiceProvider();
        using var worker = new RoomCycleService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RoomCycleService>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            var roundNumber = 0;
            while (DateTime.UtcNow < deadline)
            {
                await using var check = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>()
                    .UseSqlite(connectionString).Options);
                roundNumber = await check.Rooms.AsNoTracking().Where(room => room.Id == 1)
                    .Select(room => room.RoundNumber).SingleAsync();
                if (roundNumber > 0) break;
                await Task.Delay(50);
            }

            Assert.Equal(1, roundNumber);
            Assert.Contains(test.LogStore.Get(1), log => log.Text.Contains("1号位") && log.Text.Contains("普通攻击"));
            Assert.Contains(test.LogStore.Get(1), log => log.Text.Contains("2号位") && log.Text.Contains("普通攻击"));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class AutoBattleTestContext : IAsyncDisposable
    {
        private readonly string _path;

        private AutoBattleTestContext(string path, GameDbContext db, Room room, BattleService battleService,
            RoomService roomService, BattleLogStore logStore)
        {
            _path = path;
            Db = db;
            Room = room;
            BattleService = battleService;
            RoomService = roomService;
            LogStore = logStore;
        }

        public string Token => "token";
        public GameDbContext Db { get; }
        public Room Room { get; }
        public BattleService BattleService { get; }
        public RoomService RoomService { get; }
        public BattleLogStore LogStore { get; }

        public static async Task<AutoBattleTestContext> CreateAsync(bool isAutoEnabled)
        {
            var path = Path.Combine(Path.GetTempPath(), $"idlegame-auto-flow-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<GameDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options;
            var db = new GameDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var room = new Room
            {
                Id = 1, DungeonId = 1, MonsterId = 1, OwnerUserId = 1, SlotCount = 5,
                Status = RoomStatus.NotStarted, PreparationStartedAtUtc = DateTime.UtcNow
            };
            db.AddRange(
                new Dungeon { Id = 1, Code = "slime-field", Name = "史莱姆平原", MonsterName = "Slime", MonsterMaxHp = 50, MonsterAttack = 1, MonsterDefense = 5, SlotCount = 5, SortOrder = 1 },
                new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 },
                new Character { Id = 1, UserId = 1, Name = "Knight", Hp = 100, MaxHp = 100, Attack = 10},
                new Monster { Id = 1, Name = "Slime", Hp = 50, MaxHp = 50, Attack = 1, Defense = 5 },
                room,
                new RoomSlot { Id = 1, RoomId = 1, SlotIndex = 1, UserId = 1, CharacterId = 1, IsMainControl = true, IsAutoEnabled = isAutoEnabled },
                new CharacterSkillSlot { CharacterId = 1, SlotIndex = 1, SkillCode = "knight-strike", AutoUseEnabled = true, AutoHpThresholdPercent = 70 },
                new UserDungeonClear { UserId = 1, DungeonId = 1, ClearedAtUtc = DateTime.UtcNow },
                new CharacterBattleMilestone { CharacterId = 1, Kind = BattleMilestoneService.DungeonClearKind, TargetCode = "slime-field", Count = 1, FirstAtUtc = DateTime.UtcNow, LastAtUtc = DateTime.UtcNow },
                new UserLoginSession { Id = 1, UserId = 1, Token = "token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();

            var progression = ProgressionTestFactory.Create();
            var skills = SkillTestFactory.Create();
            var rewards = RewardTestFactory.CreateService(db, progression);
            var logs = new BattleLogStore();
            var users = new UserService(db, progression, skills);
            var battle = new BattleService(db, users, ConsumableTestFactory.Create(), skills, rewards,
                battleLogStore: logs);
            var rooms = new RoomService(db, users, progression, ConsumableTestFactory.Create(), skills, rewards,
                battleLogStore: logs);
            return new AutoBattleTestContext(path, db, room, battle, rooms, logs);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            File.Delete(_path);
        }
    }
}
