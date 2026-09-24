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
    public async Task SwordTreeAllowsCrossBranchChoicesAndKeepsOnlyStancesExclusive()
    {
        await using var test = await FormalTreeContext.CreateAsync("swordsman", level: 10, points: 9);
        Assert.Equal("SkillTalentTreePointsRequired",
            (await test.Service.UnlockTalentNodeAsync("token", 1, "sword-assault-stance")).Error);
        foreach (var code in new[] { "sword-edge", "sword-rhythm", "sword-vitality", "sword-assault-stance" })
            Assert.Null((await test.Service.UnlockTalentNodeAsync("token", 1, code)).Error);

        var (blocked, branchError) = await test.Service.UnlockTalentNodeAsync("token", 1, "sword-guard-stance");
        Assert.Null(blocked);
        Assert.Equal("SkillTalentBranchLocked", branchError);

        foreach (var code in new[] { "sword-combat-training", "sword-combat-training", "sword-intercept-talent",
                     "sword-recovery-training" })
            Assert.Null((await test.Service.UnlockTalentNodeAsync("token", 1, code)).Error);
        var (finished, finalError) = await test.Service.UnlockTalentNodeAsync("token", 1, "sword-disruption");
        Assert.Null(finalError);
        Assert.Equal(0, finished!.TalentPoints);
        Assert.Equal(9, (await test.Db.CharacterSkillTalents.ToListAsync()).Sum(node => node.PointsSpent));
        Assert.Contains(finished.LearnedSkills, skill => skill.Code == "sword-double-slash");
        var intercept = Assert.Single(finished.LearnedSkills, skill => skill.Code == "sword-intercept");
        Assert.Equal(2, intercept.CooldownRounds);
        Assert.Equal("InterruptibleIntent", intercept.AutoCondition);
        Assert.Equal(1, (await test.Db.CharacterSkillTalents.SingleAsync(node => node.NodeCode == "sword-recovery-training")).PointsSpent);
        Assert.Equal(8, test.Character.TalentSkillDamagePercent);

        Assert.Null((await test.Service.PromoteAsync("token", 1,
            new PromoteCharacterRequest { ProfessionCode = "knight" })).Error);
        test.Character.Level = 11;
        test.Character.TalentPoints++;
        await test.Db.SaveChangesAsync();
        Assert.Null((await test.Service.UnlockTalentNodeAsync("token", 1, "sword-recovery-training")).Error);
        Assert.Equal(0, test.Character.TalentPoints);
    }

    [Fact]
    public async Task RankTwoNeedsTheFollowingLevelAndResetRefundsNodes()
    {
        await using var test = await FormalTreeContext.CreateAsync("acolyte", level: 4, points: 4);
        foreach (var code in new[] { "acolyte-echo", "acolyte-prayer" })
            Assert.Null((await test.Service.UnlockTalentNodeAsync("token", 1, code)).Error);
        Assert.Null((await test.Service.UnlockTalentNodeAsync("token", 1, "acolyte-heal-training")).Error);
        var (_, levelError) = await test.Service.UnlockTalentNodeAsync("token", 1, "acolyte-heal-training");
        Assert.Equal("SkillTalentLevelRequired", levelError);

        var (reset, resetError) = await test.Service.ResetTalentTreeAsync("token", 1);
        Assert.Null(resetError);
        Assert.Equal(4, reset!.TalentPoints);
        Assert.Empty(await test.Db.CharacterSkillTalents.ToListAsync());
        Assert.Equal(0, test.Character.TalentHealingDonePercent);
    }

    [Fact]
    public async Task AcolyteCanMixDamageHealingAndThreeNewSkills()
    {
        await using var test = await FormalTreeContext.CreateAsync("acolyte", level: 10, points: 9);
        foreach (var code in new[] { "acolyte-echo", "acolyte-doctrine", "acolyte-prayer",
                     "acolyte-light-training", "acolyte-light-training", "acolyte-silence-talent",
                     "acolyte-purify-talent", "acolyte-radiant-flare-talent", "acolyte-mercy" })
            Assert.Null((await test.Service.UnlockTalentNodeAsync("token", 1, code)).Error);

        var response = await test.Service.GetAsync("token", 1);
        Assert.Null(response.Error);
        Assert.Equal(0, response.Response!.TalentPoints);
        var silence = Assert.Single(response.Response.LearnedSkills, skill => skill.Code == "acolyte-silence");
        Assert.Equal(5, silence.CooldownRounds);
        Assert.Equal("InterruptibleIntent", silence.AutoCondition);
        Assert.Contains(silence.Effects, effect => effect.Type == "Interrupt");
        Assert.Contains(silence.Effects, effect => effect.StatusCode == "acolyte-silence" && effect.DurationRounds == 1);
        Assert.Contains(response.Response.LearnedSkills, skill => skill.Code == "acolyte-purify");
        Assert.Contains(response.Response.LearnedSkills, skill => skill.Code == "acolyte-radiant-flare");
        Assert.Contains(response.Response.LearnedSkills.Single(skill => skill.Code == "acolyte-radiant-flare").Effects,
            effect => effect.StatusCode == "holy-blindness" && effect.DurationRounds == 2);
        Assert.Contains(response.Response.TalentNodes, node => node.Code == "acolyte-afterglow" && !node.CanUnlock);
    }

    [Fact]
    public async Task MageCanMixBurstDispelAndDamageSuppression()
    {
        await using var test = await FormalTreeContext.CreateAsync("mage", level: 10, points: 9);
        foreach (var code in new[] { "mage-arcane-insight", "mage-flow", "mage-frost-discipline",
                     "mage-arcane-training", "mage-arcane-training", "mage-spellbreak-talent",
                     "mage-barrage-talent", "mage-suppression-talent", "mage-countermagic" })
            Assert.Null((await test.Service.UnlockTalentNodeAsync("token", 1, code)).Error);

        var response = (await test.Service.GetAsync("token", 1)).Response!;
        Assert.Equal(0, response.TalentPoints);
        Assert.Contains(response.LearnedSkills, skill => skill.Code == "mage-arcane-barrage" && skill.Effects.Count == 3);
        Assert.Contains(response.LearnedSkills, skill => skill.Code == "mage-spellbreak" &&
            skill.AutoCondition == "MonsterHasBuff" && skill.Effects.Any(effect => effect.Type == "Dispel"));
        Assert.Contains(response.LearnedSkills, skill => skill.Code == "mage-arcane-suppression" &&
            skill.Effects.Any(effect => effect.StatusCode == "arcane-suppression"));
    }

    [Fact]
    public async Task HunterCanMixMarkPoisonAndRapidShots()
    {
        await using var test = await FormalTreeContext.CreateAsync("hunter", level: 10, points: 9);
        foreach (var code in new[] { "hunter-keen-eye", "hunter-steady-hand", "hunter-fieldcraft",
                     "hunter-bow-training", "hunter-bow-training", "hunter-venom-talent",
                     "hunter-mark-talent", "hunter-volley-talent", "hunter-toxin-training" })
            Assert.Null((await test.Service.UnlockTalentNodeAsync("token", 1, code)).Error);

        var response = (await test.Service.GetAsync("token", 1)).Response!;
        Assert.Equal(0, response.TalentPoints);
        Assert.Contains(response.LearnedSkills, skill => skill.Code == "hunter-marked-shot" &&
            skill.Effects.Any(effect => effect.StatusCode == "hunters-mark"));
        Assert.Contains(response.LearnedSkills, skill => skill.Code == "hunter-venom-arrow" &&
            skill.Effects.Any(effect => effect.StatusCode == "poison"));
        Assert.Contains(response.LearnedSkills, skill => skill.Code == "hunter-rapid-volley" && skill.Effects.Count == 2);
    }

    [Fact]
    public async Task RogueCanMixFlurryPoisonAndInterrupts()
    {
        await using var test = await FormalTreeContext.CreateAsync("rogue", level: 10, points: 9);
        foreach (var code in new[] { "rogue-killer-instinct", "rogue-light-fingers", "rogue-footwork",
                     "rogue-blade-training", "rogue-blade-training", "rogue-poison-talent",
                     "rogue-flurry-talent", "rogue-gouge-talent", "rogue-dirty-fighting" })
            Assert.Null((await test.Service.UnlockTalentNodeAsync("token", 1, code)).Error);

        var response = (await test.Service.GetAsync("token", 1)).Response!;
        Assert.Equal(0, response.TalentPoints);
        Assert.Contains(response.LearnedSkills, skill => skill.Code == "rogue-blade-flurry" && skill.Effects.Count == 2);
        Assert.Contains(response.LearnedSkills, skill => skill.Code == "rogue-poisoned-blade" &&
            skill.Effects.Any(effect => effect.StatusCode == "poison" && effect.DurationRounds == 3));
        Assert.Contains(response.LearnedSkills, skill => skill.Code == "rogue-gouge" &&
            skill.AutoCondition == "InterruptibleIntent" && skill.Effects.Any(effect => effect.Type == "Interrupt"));
        Assert.Equal(12, test.Character.TalentSkillDamagePercent);
    }

    [Fact]
    public void ProductionConfigExposesFiveBaseProfessions()
    {
        var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();
        var monsterCatalog = new MonsterCombatCatalog(Options.Create(
            config.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!));
        var catalog = new SkillCatalog(Options.Create(
            config.GetSection(SkillOptions.SectionName).Get<SkillOptions>()!), monsterCatalog);

        Assert.Equal(new[] { "acolyte", "hunter", "mage", "rogue", "swordsman" },
            catalog.BaseProfessions.Select(profession => profession.Code).Order());
    }

    [Theory]
    [InlineData("mage")]
    [InlineData("hunter")]
    public async Task ProfessionWithoutConfiguredPromotionDoesNotAdvertisePromotion(string professionCode)
    {
        await using var test = await FormalTreeContext.CreateAsync(professionCode, level: 10, points: 9);

        var response = (await test.Service.GetAsync("token", 1)).Response!;

        Assert.False(response.CanPromote);
        Assert.Empty(response.PromotionOptions);
    }

    [Fact]
    public async Task RogueAdvertisesBothPromotionPathsAtLevelTen()
    {
        await using var test = await FormalTreeContext.CreateAsync("rogue", level: 10, points: 9);

        var response = (await test.Service.GetAsync("token", 1)).Response!;

        Assert.True(response.CanPromote);
        Assert.Equal(new[] { "assassin", "trickster" }, response.PromotionOptions.Select(option => option.Code).Order());
        Assert.All(response.PromotionOptions, option => Assert.False(string.IsNullOrWhiteSpace(option.GrantedSkillName)));
    }

    [Theory]
    [InlineData("swordsman", "knight", "knight-guard")]
    [InlineData("swordsman", "warrior", "warrior-fury")]
    [InlineData("acolyte", "priest", "priest-group-heal")]
    [InlineData("rogue", "assassin", "assassin-deathblow")]
    [InlineData("rogue", "trickster", "trickster-smoke-bomb")]
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
