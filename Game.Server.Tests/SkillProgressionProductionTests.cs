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

public sealed class SkillProgressionProductionTests
{
    [Fact]
    public void ProductionProfessionsUnlockConfiguredSkillsAndRanksByLevelThirty()
    {
        var catalog = LoadProductionCatalog();
        Assert.Equal(new[] { "acolyte", "hunter", "mage", "rogue", "swordsman" },
            catalog.BaseProfessions.Select(profession => profession.Code).Order());
        Assert.DoesNotContain(catalog.Professions, profession => profession.IsPromotion);

        foreach (var profession in catalog.BaseProfessions)
        {
            var levelOne = catalog.SkillsForProfessionAtLevel(profession.Code, 1);
            var levelTen = catalog.SkillsForProfessionAtLevel(profession.Code, 10);
            var levelThirty = catalog.SkillsForProfessionAtLevel(profession.Code, 30);
            Assert.NotEmpty(levelOne);
            Assert.Equal(profession.Code == "swordsman" ? 4 : 5, levelTen.Count);
            Assert.Equal(levelTen.Count, levelThirty.Count);
            Assert.Contains(levelThirty, skill => skill.Code == profession.SharedSkillCode);
            Assert.All(levelThirty, skill =>
            {
                Assert.InRange(skill.UnlockLevel, 1, 10);
                Assert.InRange(skill.Level2UnlockLevel, 11, 20);
                Assert.InRange(skill.Level3UnlockLevel, 21, 30);
                Assert.True(skill.InitialCooldownRounds >= 0);
                Assert.Equal(3, SkillCatalog.RankFor(skill, 30));
            });
        }
    }

    [Fact]
    public async Task SkillsUnlockWithoutTalentPurchasesAndExposeRankAndInitialCooldown()
    {
        await using var test = await SkillContext.CreateAsync("mage", level: 1);
        var first = (await test.Service.GetAsync("token", 1)).Response!;
        Assert.NotEmpty(first.LearnedSkills);
        Assert.All(first.LearnedSkills, skill => Assert.Equal(1, skill.Level));

        test.Character.Level = 10;
        await test.Db.SaveChangesAsync();
        var tenth = (await test.Service.GetAsync("token", 1)).Response!;
        Assert.Equal(5, tenth.LearnedSkills.Count);
        Assert.All(tenth.LearnedSkills, skill => Assert.InRange(skill.InitialCooldownRounds, 0, 4));

        test.Character.Level = 30;
        await test.Db.SaveChangesAsync();
        var thirtieth = (await test.Service.GetAsync("token", 1)).Response!;
        Assert.All(thirtieth.LearnedSkills, skill => Assert.Equal(3, skill.Level));
        Assert.Empty(await test.Db.CharacterSkillTalents.ToListAsync());
    }

    [Fact]
    public async Task SharedPoolBelongsToCharacterAndOnlyOneForeignSkillCanBeEquipped()
    {
        await using var test = await SkillContext.CreateAsync("mage", level: 9);
        test.Db.CharacterCombatProfessions.AddRange(
            new CharacterCombatProfession { CharacterId = 1, ProfessionCode = "swordsman", Level = 30 },
            new CharacterCombatProfession { CharacterId = 1, ProfessionCode = "hunter", Level = 30 });
        test.Db.Characters.Add(new Character { Id = 2, UserId = 1, Name = "Other", ProfessionCode = "mage",
            Level = 10, Hp = 100, MaxHp = 100, Attack = 20 });
        await test.Db.SaveChangesAsync();

        var locked = (await test.Service.GetAsync("token", 1)).Response!;
        Assert.Equal(2, locked.SharedSkills.Count);
        Assert.All(locked.SharedSkills, skill => Assert.False(skill.CanEquip));
        Assert.Equal("SkillNotLearned", (await test.Service.SetSlotAsync("token", 1, 1,
            new SetSkillSlotRequest { SkillCode = "sword-intercept" })).Error);
        Assert.Empty((await test.Service.GetAsync("token", 2)).Response!.SharedSkills);

        test.Character.Level = 10;
        await test.Db.SaveChangesAsync();
        Assert.All((await test.Service.GetAsync("token", 1)).Response!.SharedSkills,
            skill => Assert.True(skill.CanEquip));
        Assert.Null((await test.Service.SetSlotAsync("token", 1, 1,
            new SetSkillSlotRequest { SkillCode = "sword-intercept" })).Error);
        Assert.Equal("SharedSkillLimitReached", (await test.Service.SetSlotAsync("token", 1, 2,
            new SetSkillSlotRequest { SkillCode = "hunter-rapid-volley" })).Error);
        Assert.Contains((await test.Service.GetAsync("token", 1)).Response!.Slots,
            slot => slot.SkillCode == "sword-intercept");
    }

    private static SkillCatalog LoadProductionCatalog()
    {
        var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();
        var monsters = new MonsterCombatCatalog(Options.Create(
            config.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!));
        return new SkillCatalog(Options.Create(config.GetSection(SkillOptions.SectionName).Get<SkillOptions>()!), monsters);
    }

    private sealed class SkillContext : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private SkillContext(SqliteConnection connection, GameDbContext db, Character character, SkillService service)
            => (_connection, Db, Character, Service) = (connection, db, character, service);

        public GameDbContext Db { get; }
        public Character Character { get; }
        public SkillService Service { get; }

        public static async Task<SkillContext> CreateAsync(string profession, int level)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var catalog = LoadProductionCatalog();
            var character = new Character { Id = 1, UserId = 1, Name = "Tester", ProfessionCode = profession,
                Level = level, Hp = 100, MaxHp = 100, Attack = 20 };
            db.AddRange(new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 }, character,
                new UserLoginSession { UserId = 1, Token = "token", CreatedAt = DateTime.UtcNow,
                    ExpireAt = DateTime.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();
            var users = new UserService(db, ProgressionTestFactory.Create(), catalog);
            return new SkillContext(connection, db, character, new SkillService(db, users, catalog));
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
