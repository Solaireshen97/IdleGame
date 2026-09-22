using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
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
