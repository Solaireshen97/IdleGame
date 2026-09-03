using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public class BattleServiceTests
{
    [Fact]
    public async Task ExecuteRoundAsync_FirstRound_AppliesBothAttacks()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var (result, error) = await test.Service.ExecuteRoundAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(93, result!.CharacterHp);
        Assert.Equal(35, result.MonsterHp);
        Assert.Equal(RoomStatus.Cooldown, result.RoomStatus);
    }

    [Fact]
    public async Task ExecuteRoundAsync_DamageBelowDefense_DealsAtLeastOne()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterDefense: 99, monsterAttack: 1, characterDefense: 99);
        var (result, error) = await test.Service.ExecuteRoundAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(49, result!.MonsterHp);
        Assert.Equal(99, result.CharacterHp);
    }

    [Fact]
    public async Task ExecuteRoundAsync_PlayerKillsMonster_DoesNotCounterattack()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        var (result, error) = await test.Service.ExecuteRoundAsync(1, test.Token);

        Assert.Null(error);
        Assert.True(result!.IsVictory);
        Assert.Equal(100, result.CharacterHp);
        Assert.Equal(RoomStatus.BattleOver, result.RoomStatus);
        Assert.NotNull(result.BattleEndedAtUtc);
    }

    [Fact]
    public async Task ExecuteRoundAsync_MonsterKillsPlayer_SetsBattleOver()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 5, monsterAttack: 100);
        var (result, error) = await test.Service.ExecuteRoundAsync(1, test.Token);

        Assert.Null(error);
        Assert.True(result!.IsCharacterDead);
        Assert.Equal(0, result.CharacterHp);
        Assert.Equal(RoomStatus.BattleOver, result.RoomStatus);
    }

    [Fact]
    public async Task ExecuteRoundAsync_DuringCooldown_DoesNotChangeHp()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var (firstResult, _) = await test.Service.ExecuteRoundAsync(1, test.Token);
        var (result, error) = await test.Service.ExecuteRoundAsync(1, test.Token);

        Assert.Equal("RoundCooldown", error);
        Assert.Equal(firstResult!.CharacterHp, result!.CharacterHp);
        Assert.Equal(firstResult.MonsterHp, result.MonsterHp);
    }

    [Fact]
    public async Task ExecuteRoundAsync_AfterCooldown_CanExecuteAgain()
    {
        await using var test = await BattleTestContext.CreateAsync();
        await test.Service.ExecuteRoundAsync(1, test.Token);
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        test.Room.Version++;
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.ExecuteRoundAsync(1, test.Token);
        Assert.Null(error);
        Assert.Equal(20, result!.MonsterHp);
    }

    [Fact]
    public async Task ExecuteRoundAsync_AfterBattleOver_IsRejectedWithoutChanges()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        var (firstResult, _) = await test.Service.ExecuteRoundAsync(1, test.Token);
        var (result, error) = await test.Service.ExecuteRoundAsync(1, test.Token);

        Assert.Equal("BattleOver", error);
        Assert.Equal(firstResult!.MonsterHp, result!.MonsterHp);
    }

    [Fact]
    public async Task ResetBattleAsync_RestoresMonsterAndClearsRoundState()
    {
        await using var test = await BattleTestContext.CreateAsync();
        await test.Service.ExecuteRoundAsync(1, test.Token);

        var (success, error) = await test.Service.ResetBattleAsync(1, test.Token);
        Assert.True(success);
        Assert.Null(error);
        Assert.Equal(50, test.Monster.Hp);
        Assert.Equal(RoomStatus.NotStarted, test.Room.Status);
        Assert.Null(test.Room.NextRoundAvailableAtUtc);
        Assert.Null(test.Room.BattleEndedAtUtc);
    }

    [Fact]
    public async Task HealCharacterAsync_ClampsHpAndPreservesRoomState()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 95);
        test.Room.Status = RoomStatus.Cooldown;
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(10);
        await test.Db.SaveChangesAsync();

        var (success, error) = await test.Service.HealCharacterAsync(1, test.Token);
        Assert.True(success);
        Assert.Null(error);
        Assert.Equal(100, test.Character.Hp);
        Assert.Equal(RoomStatus.Cooldown, test.Room.Status);
        Assert.NotNull(test.Room.NextRoundAvailableAtUtc);
    }

    [Fact]
    public async Task ExecuteRoundAsync_NonMember_IsRejectedWithoutChanges()
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Db.Users.Add(new User { Id = 2, UserName = "other", PasswordHash = "x", ActiveCharacterId = 2 });
        test.Db.Characters.Add(new Character { Id = 2, UserId = 2, Name = "Other", Hp = 100, MaxHp = 100, Attack = 20, Defense = 5 });
        test.Db.UserLoginSessions.Add(new UserLoginSession { UserId = 2, Token = "other-token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.ExecuteRoundAsync(1, "other-token");
        Assert.Null(result);
        Assert.Equal("NotInRoom", error);
        Assert.Equal(50, test.Monster.Hp);
        Assert.Equal(100, test.Character.Hp);
    }

    [Fact]
    public async Task ExecuteRoundAsync_ConcurrentRequests_OnlyOneSucceeds()
    {
        await using var test = await BattleTestContext.CreateAsync();
        await using var firstDb = test.CreateDbContext();
        await using var secondDb = test.CreateDbContext();
        var firstService = new BattleService(firstDb, new UserService(firstDb));
        var secondService = new BattleService(secondDb, new UserService(secondDb));

        var results = await Task.WhenAll(firstService.ExecuteRoundAsync(1, test.Token), secondService.ExecuteRoundAsync(1, test.Token));
        Assert.Single(results.Where(result => result.Error is null));
    }

    [Fact]
    public async Task ExecuteRoundAsync_UsesSlotOrderForPartyAttacks()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1, characterDefense: 99);
        await test.AddSlotAsync(2, "Mage", attack: 10);

        var (result, error) = await test.Service.ExecuteRoundAsync(1, test.Token);

        Assert.Null(error);
        Assert.StartsWith("Slot 1 Knight attacks", result!.Logs[0]);
        Assert.StartsWith("Slot 2 Mage attacks", result.Logs[1]);
    }

    [Fact]
    public async Task ExecuteRoundAsync_StopsLaterSlotsWhenMonsterDies()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        await test.AddSlotAsync(2, "Mage", attack: 100);

        var (result, error) = await test.Service.ExecuteRoundAsync(1, test.Token);

        Assert.Null(error);
        Assert.Single(result!.Logs.Where(x => x.Contains("attacks Slime")));
        Assert.DoesNotContain(result.Logs, x => x.Contains("Slot 2 Mage attacks"));
        Assert.DoesNotContain(result.Logs, x => x.Contains("Slime attacks"));
    }

    [Fact]
    public async Task ExecuteRoundAsync_TargetsNextLivingLowestSlot()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 5, monsterAttack: 100);
        var second = await test.AddSlotAsync(2, "Mage", defense: 5);

        await test.Service.ExecuteRoundAsync(1, test.Token);
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        test.Room.Version++;
        await test.Db.SaveChangesAsync();
        var (result, error) = await test.Service.ExecuteRoundAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(0, test.Character.Hp);
        Assert.Equal(5, second.Hp);
        Assert.Contains(result!.Logs, x => x.Contains("attacks Slot 2 Mage"));
    }

    private sealed class BattleTestContext : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly DbContextOptions<GameDbContext> _options;

        private BattleTestContext(string databasePath, DbContextOptions<GameDbContext> options, GameDbContext db, Room room, Character character, Monster monster)
        {
            _databasePath = databasePath;
            _options = options;
            Db = db;
            Room = room;
            Character = character;
            Monster = monster;
            Service = new BattleService(db, new UserService(db));
        }

        public string Token => "token";
        public GameDbContext Db { get; }
        public Room Room { get; }
        public Character Character { get; }
        public Monster Monster { get; }
        public BattleService Service { get; }

        public async Task<Character> AddSlotAsync(int slotIndex, string name, int hp = 100, int attack = 20, int defense = 5)
        {
            var character = new Character { UserId = 1, Name = name, Hp = hp, MaxHp = 100, Attack = attack, Defense = defense };
            Db.Characters.Add(character);
            await Db.SaveChangesAsync();
            Db.RoomSlots.Add(new RoomSlot { RoomId = Room.Id, SlotIndex = slotIndex, CharacterId = character.Id, UserId = 1 });
            await Db.SaveChangesAsync();
            return character;
        }

        public static async Task<BattleTestContext> CreateAsync(int characterHp = 100, int characterAttack = 20, int characterDefense = 5, int monsterAttack = 12, int monsterDefense = 5)
        {
            var path = Path.Combine(Path.GetTempPath(), $"idlegame-tests-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            var db = new GameDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var user = new User { Id = 1, UserName = "user", PasswordHash = "x", ActiveCharacterId = 1 };
            var character = new Character { Id = 1, UserId = 1, Name = "Knight", Hp = characterHp, MaxHp = 100, Attack = characterAttack, Defense = characterDefense };
            var monster = new Monster { Id = 1, Name = "Slime", Hp = 50, MaxHp = 50, Attack = monsterAttack, Defense = monsterDefense };
            var room = new Room { Id = 1, MonsterId = 1, OwnerUserId = 1, SlotCount = 5, Status = RoomStatus.NotStarted };
            db.AddRange(user, character, monster, room, new RoomSlot { Id = 1, RoomId = 1, SlotIndex = 1, UserId = 1, CharacterId = 1, IsMainControl = true }, new UserLoginSession { Id = 1, UserId = 1, Token = "token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();
            return new BattleTestContext(path, options, db, room, character, monster);
        }

        public GameDbContext CreateDbContext() => new(_options);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            File.Delete(_databasePath);
        }
    }
}
