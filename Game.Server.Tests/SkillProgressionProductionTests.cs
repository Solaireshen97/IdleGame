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
            Assert.Equal(5, levelTen.Count);
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
    public void KnightCatalogHasFiveSkillsWithExactRankProgressionAndSharedRebuke()
    {
        var catalog = LoadProductionCatalog();
        var knight = catalog.FindProfession("swordsman")!;
        Assert.Equal(new[] { "sword-slash" }, knight.StartingSkills);
        Assert.Equal("knight-rebuke", knight.SharedSkillCode);
        Assert.Equal("信仰壁垒", catalog.FindSkill("knight-faith-barrier")!.Name);
        Assert.Equal("责难", catalog.FindSkill("knight-rebuke")!.Name);

        var expected = new[]
        {
            (Code: "sword-slash", Unlock: 1, Rank2: 12, Rank3: 22, Initial: 1, Cooldown: 3),
            (Code: "knight-faith-barrier", Unlock: 3, Rank2: 14, Rank3: 24, Initial: 0, Cooldown: 5),
            (Code: "knight-rebuke", Unlock: 5, Rank2: 16, Rank3: 26, Initial: 0, Cooldown: 4),
            (Code: "knight-invigorate", Unlock: 7, Rank2: 18, Rank3: 28, Initial: 2, Cooldown: 5),
            (Code: "knight-holy-aura", Unlock: 10, Rank2: 20, Rank3: 30, Initial: 3, Cooldown: 6)
        };
        foreach (var item in expected)
        {
            var skill = catalog.FindSkill(item.Code)!;
            Assert.Equal((item.Unlock, item.Rank2, item.Rank3),
                (skill.UnlockLevel, skill.Level2UnlockLevel, skill.Level3UnlockLevel));
            Assert.Equal((item.Initial, item.Cooldown), (skill.InitialCooldownRounds, skill.CooldownRounds));
            Assert.DoesNotContain(catalog.SkillsForProfessionAtLevel("swordsman", item.Unlock - 1),
                candidate => candidate.Code == item.Code);
            Assert.Equal(1, SkillCatalog.RankFor(skill, item.Rank2 - 1));
            Assert.Equal(2, SkillCatalog.RankFor(skill, item.Rank2));
            Assert.Equal(3, SkillCatalog.RankFor(skill, item.Rank3));
        }

        var rank1 = catalog.SkillsForProfessionAtLevel("swordsman", 10).ToDictionary(skill => skill.Code);
        var rank2 = catalog.SkillsForProfessionAtLevel("swordsman", 20).ToDictionary(skill => skill.Code);
        var rank3 = catalog.SkillsForProfessionAtLevel("swordsman", 30).ToDictionary(skill => skill.Code);
        Assert.Equal(50, Effect(rank1["sword-slash"], "Damage").AttackPowerPercent);
        Assert.Equal(60, Effect(rank2["sword-slash"], "Damage").AttackPowerPercent);
        Assert.Equal(15, Effect(rank3["sword-slash"], "Guard").Power);
        Assert.Equal(10, Effect(rank1["knight-faith-barrier"], "Guard", "AllOtherAlive").Power);
        Assert.Equal(15, Effect(rank2["knight-faith-barrier"], "Guard", "AllOtherAlive").Power);
        Assert.Equal(4, rank3["knight-faith-barrier"].CooldownRounds);
        Assert.Equal(30, Effect(rank1["knight-rebuke"], "Damage").AttackPowerPercent);
        Assert.Equal(40, Effect(rank2["knight-rebuke"], "Damage").AttackPowerPercent);
        Assert.Equal(50, Effect(rank3["knight-rebuke"], "Damage").AttackPowerPercent);
        Assert.Equal("PreferInterrupt", rank3["knight-rebuke"].AutoCondition);
        Assert.Equal(8, Effect(rank1["knight-invigorate"], "Heal", "LowestHpAllyFixed").HealMaxHpPercent);
        Assert.Equal(10, Effect(rank2["knight-invigorate"], "Heal", "LowestHpAllyFixed").HealMaxHpPercent);
        Assert.Equal(4, rank3["knight-invigorate"].CooldownRounds);
        Assert.Equal(20, Effect(rank1["knight-holy-aura"], "ApplyStatus", "Monster").AttackPowerPercent);
        Assert.Equal(6, Effect(rank2["knight-holy-aura"], "ApplyStatus", "AllAlive").HealMaxHpPercent);
        Assert.Equal(30, Effect(rank3["knight-holy-aura"], "ApplyStatus", "Monster").AttackPowerPercent);

        var sharedOwner = new Character { ProfessionCode = "mage", Level = 10 };
        var shared = catalog.ResolveSkillForLevel(sharedOwner, "knight-rebuke",
            new Dictionary<string, int> { ["swordsman"] = 30 })!;
        Assert.Equal(10, Effect(shared, "Damage").AttackPowerPercent);
        Assert.Equal("InterruptibleIntent", shared.AutoCondition);
    }

    private static CombatSkillEffectOptions Effect(CombatSkillOptions skill, string type, string? target = null) =>
        Assert.Single(SkillCatalog.EffectsFor(skill), effect => effect.Type == type &&
            (target is null || effect.Target == target));

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
            new SetSkillSlotRequest { SkillCode = "knight-rebuke" })).Error);
        Assert.Empty((await test.Service.GetAsync("token", 2)).Response!.SharedSkills);

        test.Character.Level = 10;
        await test.Db.SaveChangesAsync();
        Assert.All((await test.Service.GetAsync("token", 1)).Response!.SharedSkills,
            skill => Assert.True(skill.CanEquip));
        Assert.Null((await test.Service.SetSlotAsync("token", 1, 1,
            new SetSkillSlotRequest { SkillCode = "knight-rebuke" })).Error);
        Assert.Equal("SharedSkillLimitReached", (await test.Service.SetSlotAsync("token", 1, 2,
            new SetSkillSlotRequest { SkillCode = "hunter-rapid-volley" })).Error);
        Assert.Contains((await test.Service.GetAsync("token", 1)).Response!.Slots,
            slot => slot.SkillCode == "knight-rebuke");
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
