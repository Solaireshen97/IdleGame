using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public class TalentServiceTests
{
    [Fact]
    public async Task AllocateAndReset_UseIncreasingCostsAndPreserveCurrentHp()
    {
        await using var test = await TalentTestContext.CreateAsync(points: 6, hp: 98);
        var (first, firstError) = await test.Service.AllocateAsync("token", 1, TalentType.Health);
        var (second, secondError) = await test.Service.AllocateAsync("token", 1, TalentType.Health);
        var (third, thirdError) = await test.Service.AllocateAsync("token", 1, TalentType.Health);

        Assert.Null(firstError);
        Assert.Null(secondError);
        Assert.Null(thirdError);
        Assert.Equal((5, 105, 98), (first!.TalentPoints, first.MaxHp, first.Hp));
        Assert.Equal((3, 110), (second!.TalentPoints, second.MaxHp));
        Assert.Equal((0, 115), (third!.TalentPoints, third.MaxHp));
        Assert.Equal(3, test.Character.HealthTalentRank);
        Assert.Equal(100, test.Character.MaxHp);

        var (overCap, overCapError) = await test.Service.AllocateAsync("token", 1, TalentType.Health);
        Assert.Null(overCap);
        Assert.Equal("TalentMaxRank", overCapError);

        test.Character.Hp = 113;
        await test.Db.SaveChangesAsync();
        var (reset, resetError) = await test.Service.ResetAsync("token", 1);
        Assert.Null(resetError);
        Assert.Equal((6, 100, 100), (reset!.TalentPoints, reset.MaxHp, reset.Hp));
        Assert.All(reset.Talents, node => Assert.Equal(0, node.Rank));
        var (again, _) = await test.Service.ResetAsync("token", 1);
        Assert.Equal(6, again!.TalentPoints);
    }

    [Fact]
    public async Task Allocate_RejectsInsufficientPointsAndOtherPlayersCharacter()
    {
        await using var test = await TalentTestContext.CreateAsync(points: 1);
        var (first, _) = await test.Service.AllocateAsync("token", 1, TalentType.Attack);
        var (second, error) = await test.Service.AllocateAsync("token", 1, TalentType.Attack);
        var (foreign, ownershipError) = await test.Service.GetAsync("other-token", 1);

        Assert.Equal(21, first!.Attack);
        Assert.Equal(0, first.TalentPoints);
        Assert.Null(second);
        Assert.Equal("InsufficientTalentPoints", error);
        Assert.Null(foreign);
        Assert.Equal("NotOwner", ownershipError);
        Assert.Equal((20, 1), (test.Character.Attack, test.Character.AttackTalentRank));
    }

    [Fact]
    public async Task Allocate_ConcurrentRequestsCannotSpendTheSamePoint()
    {
        await using var test = await TalentTestContext.CreateAsync(points: 1);
        await using var otherDb = test.CreateDbContext();
        var otherService = new TalentService(otherDb, new UserService(otherDb, ProgressionTestFactory.Create(), SkillTestFactory.Create()));
        await test.Service.GetAsync("token", 1);
        await otherService.GetAsync("token", 1);

        var (first, _) = await test.Service.AllocateAsync("token", 1, TalentType.Attack);
        var (second, error) = await otherService.AllocateAsync("token", 1, TalentType.Defense);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal("ConcurrencyConflict", error);
        await using var verify = test.CreateDbContext();
        var character = await verify.Characters.SingleAsync();
        Assert.Equal((0, 1, 0), (character.TalentPoints, character.AttackTalentRank, character.DefenseTalentRank));
    }

    private sealed class TalentTestContext : IAsyncDisposable
    {
        private readonly string _path;
        private readonly DbContextOptions<GameDbContext> _options;

        private TalentTestContext(string path, DbContextOptions<GameDbContext> options, GameDbContext db, Character character)
        {
            _path = path;
            _options = options;
            Db = db;
            Character = character;
            Service = new TalentService(db, new UserService(db, ProgressionTestFactory.Create(), SkillTestFactory.Create()));
        }

        public GameDbContext Db { get; }
        public Character Character { get; }
        public TalentService Service { get; }
        public GameDbContext CreateDbContext() => new(_options);

        public static async Task<TalentTestContext> CreateAsync(int points, int hp = 100)
        {
            var path = Path.Combine(Path.GetTempPath(), $"idlegame-talents-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            var db = new GameDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var character = new Character { Id = 1, UserId = 1, Name = "Knight", Level = points + 1, TalentPoints = points, Hp = hp, MaxHp = 100, Attack = 20, Defense = 5 };
            db.AddRange(
                new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 },
                new User { Id = 2, UserName = "other", PasswordHash = "x", ActiveCharacterId = null },
                character,
                new UserLoginSession { UserId = 1, Token = "token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) },
                new UserLoginSession { UserId = 2, Token = "other-token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();
            return new TalentTestContext(path, options, db, character);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            File.Delete(_path);
        }
    }
}
