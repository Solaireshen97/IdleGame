using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Characters;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class SkillTalentTreeTests
{
    [Fact]
    public async Task SwordTreeUsesLevelGatesSharedPointsAndExclusiveBranches()
    {
        await using var test = await FormalTreeContext.CreateAsync("swordsman", level: 10, points: 9);
        foreach (var code in new[] { "sword-rhythm", "sword-edge", "sword-vitality", "sword-assault-stance" })
            Assert.Null((await test.Service.UnlockTalentNodeAsync("token", 1, code)).Error);

        var (blocked, branchError) = await test.Service.UnlockTalentNodeAsync("token", 1, "sword-guard-stance");
        Assert.Null(blocked);
        Assert.Equal("SkillTalentBranchLocked", branchError);

        Assert.Null((await test.Service.UnlockTalentNodeAsync("token", 1, "sword-combat-training")).Error);
        Assert.Null((await test.Service.UnlockTalentNodeAsync("token", 1, "sword-combat-training")).Error);
        Assert.Null((await test.Service.UnlockTalentNodeAsync("token", 1, "sword-pursuit")).Error);
        Assert.Null((await test.Service.UnlockTalentNodeAsync("token", 1, "sword-precision")).Error);
        var (finished, finalError) = await test.Service.UnlockTalentNodeAsync("token", 1, "sword-precision");
        Assert.Null(finalError);
        Assert.Equal(0, finished!.TalentPoints);
        Assert.Equal(9, (await test.Db.CharacterSkillTalents.ToListAsync()).Sum(node => node.PointsSpent));
        Assert.Contains(finished.LearnedSkills, skill => skill.Code == "sword-double-slash");
        Assert.Equal(8, test.Character.TalentSkillDamagePercent);
        Assert.Equal(6, test.Character.TalentSkillCriticalChancePercent);
    }

    [Fact]
    public async Task RankTwoNeedsTheFollowingLevelAndResetRefundsNodes()
    {
        await using var test = await FormalTreeContext.CreateAsync("acolyte", level: 6, points: 6);
        foreach (var code in new[] { "acolyte-echo", "acolyte-doctrine", "acolyte-prayer", "acolyte-mercy" })
            Assert.Null((await test.Service.UnlockTalentNodeAsync("token", 1, code)).Error);
        Assert.Null((await test.Service.UnlockTalentNodeAsync("token", 1, "acolyte-heal-training")).Error);
        var (_, levelError) = await test.Service.UnlockTalentNodeAsync("token", 1, "acolyte-heal-training");
        Assert.Equal("SkillTalentLevelRequired", levelError);

        var (reset, resetError) = await test.Service.ResetTalentTreeAsync("token", 1);
        Assert.Null(resetError);
        Assert.Equal(6, reset!.TalentPoints);
        Assert.Empty(await test.Db.CharacterSkillTalents.ToListAsync());
        Assert.Equal(0, test.Character.TalentHealingDonePercent);
    }

    [Theory]
    [InlineData("swordsman", "knight", "knight-guard")]
    [InlineData("swordsman", "warrior", "warrior-fury")]
    [InlineData("acolyte", "priest", "priest-group-heal")]
    public async Task LevelTenPromotionIsFreeAndGrantsBaseSkill(string baseCode, string advancedCode, string skillCode)
    {
        await using var test = await FormalTreeContext.CreateAsync(baseCode, level: 10, points: 9);
        var (response, error) = await test.Service.PromoteAsync("token", 1, new PromoteCharacterRequest { ProfessionCode = advancedCode });
        Assert.Null(error);
        Assert.Equal(advancedCode, test.Character.AdvancedProfessionCode);
        Assert.Equal(9, test.Character.TalentPoints);
        Assert.Contains(response!.LearnedSkills, skill => skill.Code == skillCode);
        Assert.Contains(response.Slots, slot => slot.SkillCode == skillCode);
        Assert.Equal(1, (await test.Db.CharacterSkillSlots.SingleAsync(slot => slot.SkillCode == skillCode)).Version);
        Assert.False(response.CanPromote);
    }

    [Fact]
    public async Task PromotionRejectsEarlyAndRepeatedRequests()
    {
        await using var test = await FormalTreeContext.CreateAsync("swordsman", level: 9, points: 8);
        Assert.Equal("PromotionLevelRequired", (await test.Service.PromoteAsync("token", 1,
            new PromoteCharacterRequest { ProfessionCode = "knight" })).Error);
        test.Character.Level = 10;
        await test.Db.SaveChangesAsync();
        Assert.Null((await test.Service.PromoteAsync("token", 1, new PromoteCharacterRequest { ProfessionCode = "knight" })).Error);
        Assert.Equal("AlreadyPromoted", (await test.Service.PromoteAsync("token", 1,
            new PromoteCharacterRequest { ProfessionCode = "warrior" })).Error);
    }

    private sealed class FormalTreeContext : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private FormalTreeContext(SqliteConnection connection, GameDbContext db, Character character, SkillService service)
            => (_connection, Db, Character, Service) = (connection, db, character, service);
        public GameDbContext Db { get; }
        public Character Character { get; }
        public SkillService Service { get; }

        public static async Task<FormalTreeContext> CreateAsync(string profession, int level, int points)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();
            var monsterCatalog = new MonsterCombatCatalog(Options.Create(config.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!));
            var catalog = new SkillCatalog(Options.Create(config.GetSection(SkillOptions.SectionName).Get<SkillOptions>()!), monsterCatalog);
            var character = new Character { Id = 1, UserId = 1, Name = "Tester", ProfessionCode = profession,
                Level = level, TalentPoints = points, Hp = 100, MaxHp = 100, Attack = 20 };
            db.AddRange(new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 }, character,
                new UserLoginSession { UserId = 1, Token = "token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            var baseSkills = catalog.FindProfession(profession)!.StartingSkills;
            for (var index = 0; index < baseSkills.Count; index++) db.CharacterSkillSlots.Add(new CharacterSkillSlot
                { CharacterId = 1, SlotIndex = index + 1, SkillCode = baseSkills[index], AutoHpThresholdPercent = 50 });
            await db.SaveChangesAsync();
            var userService = new UserService(db, ProgressionTestFactory.Create(), catalog);
            return new FormalTreeContext(connection, db, character, new SkillService(db, userService, catalog));
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }
}
