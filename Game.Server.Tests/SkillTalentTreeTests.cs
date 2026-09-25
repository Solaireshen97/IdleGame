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
    public async Task MageCanMixBurstArmorBreakDispelAndDamageSuppression()
    {
        await using var test = await FormalTreeContext.CreateAsync("mage", level: 10, points: 9);
        foreach (var code in new[] { "mage-arcane-insight", "mage-flow", "mage-frost-discipline",
                     "mage-arcane-training", "mage-arcane-training", "mage-spellbreak-talent",
                     "mage-barrage-talent", "mage-suppression-talent", "mage-countermagic" })
            Assert.Null((await test.Service.UnlockTalentNodeAsync("token", 1, code)).Error);

        var response = (await test.Service.GetAsync("token", 1)).Response!;
        Assert.Equal(0, response.TalentPoints);
        Assert.Contains(response.LearnedSkills, skill => skill.Code == "mage-arcane-barrage" &&
            skill.Effects.Count(effect => effect.Type == "Damage") == 3 &&
            skill.Effects.Any(effect => effect.StatusCode == "armor-break" && effect.DurationRounds == 2));
        Assert.Contains(response.LearnedSkills, skill => skill.Code == "mage-spellbreak" &&
            skill.AutoCondition == "MonsterHasBuff" &&
            skill.Effects.Any(effect => effect.Type == "Dispel"));
        Assert.Contains(response.LearnedSkills, skill => skill.Code == "mage-arcane-suppression" &&
            skill.Effects.Any(effect => effect.StatusCode == "arcane-suppression"));
    }

    [Fact]
    public async Task HunterCanMixMarkPoisonAndBackupInterrupts()
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
        Assert.Contains(response.LearnedSkills, skill => skill.Code == "hunter-rapid-volley" &&
            skill.AutoCondition == "InterruptibleIntent" && skill.Effects.Count(effect => effect.Type == "Damage") == 2 &&
            skill.Effects.Any(effect => effect.Type == "Interrupt"));
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
    public void ProductionConfigExposesFiveBaseProfessionsAndTenPromotionPaths()
    {
        var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();
        var monsterCatalog = new MonsterCombatCatalog(Options.Create(
            config.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!));
        var catalog = new SkillCatalog(Options.Create(
            config.GetSection(SkillOptions.SectionName).Get<SkillOptions>()!), monsterCatalog);

        Assert.Equal(new[] { "acolyte", "hunter", "mage", "rogue", "swordsman" },
            catalog.BaseProfessions.Select(profession => profession.Code).Order());
        Assert.Equal(10, catalog.Professions.Count(profession => profession.IsPromotion));
        Assert.All(catalog.BaseProfessions,
            profession => Assert.Equal(2, catalog.PromotionsFor(profession.Code).Count));
    }

    [Fact]
    public void ProductionTalentTreesAreCrossLinkedAndOfferDiverseLevelTenBuilds()
    {
        var catalog = LoadProductionCatalog();

        foreach (var profession in catalog.BaseProfessions)
        {
            var nodes = catalog.TalentNodesForProfession(profession.Code);
            var byCode = nodes.ToDictionary(node => node.Code, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(3, nodes.Count(node => node.Tier == 1));
            Assert.All(nodes.Where(node => node.Tier > 1), node => Assert.True(node.AnyPrerequisites.Count >= 2,
                $"{node.Code} should have at least two entry routes."));
            var crossLinks = nodes.Sum(node => node.Prerequisites.Concat(node.AnyPrerequisites)
                .Count(code => byCode[code].Column != node.Column));
            Assert.True(crossLinks >= 10, $"{profession.Code} only has {crossLinks} cross-branch links.");

            var builds = EnumerateLevelTenBuilds(nodes);
            Assert.True(builds.Count >= 100, $"{profession.Code} only has {builds.Count} legal nine-point builds.");
            Assert.All(nodes.Where(node => node.Tier == nodes.Max(candidate => candidate.Tier)), capstone =>
                Assert.Contains(builds, build => build.GetValueOrDefault(capstone.Code) > 0));
            Assert.True(builds.Count(build => nodes.Where(node => build.GetValueOrDefault(node.Code) > 0)
                    .Select(node => node.BranchCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 3) >= 20,
                $"{profession.Code} does not offer enough three-branch hybrid builds.");
        }
    }

    [Fact]
    public void RequiredAndAnyPrerequisitesAreBothEnforced()
    {
        var catalog = LoadProductionCatalog();
        var capstone = catalog.FindTalentNode("mage-arcane-mastery")!;

        Assert.False(catalog.ArePrerequisitesMet(capstone, new Dictionary<string, int>
            { ["mage-barrage-talent"] = 1 }));
        Assert.False(catalog.ArePrerequisitesMet(capstone, new Dictionary<string, int>
            { ["mage-precision"] = 2 }));
        Assert.True(catalog.ArePrerequisitesMet(capstone, new Dictionary<string, int>
            { ["mage-barrage-talent"] = 1, ["mage-countermagic"] = 2 }));
    }

    [Fact]
    public async Task AnyPrerequisiteAllowsEnteringAnAdjacentBranch()
    {
        await using var test = await FormalTreeContext.CreateAsync("mage", level: 4, points: 3);
        Assert.Null((await test.Service.UnlockTalentNodeAsync("token", 1, "mage-arcane-insight")).Error);
        Assert.Null((await test.Service.UnlockTalentNodeAsync("token", 1, "mage-frost-discipline")).Error);

        var (response, error) = await test.Service.UnlockTalentNodeAsync("token", 1, "mage-spellbreak-talent");

        Assert.Null(error);
        Assert.Equal(0, response!.TalentPoints);
        Assert.Contains(response.LearnedSkills, skill => skill.Code == "mage-spellbreak");
        Assert.DoesNotContain(await test.Db.CharacterSkillTalents.ToListAsync(),
            node => node.NodeCode == "mage-flow");
    }

    [Fact]
    public void ProductionConfigGivesEveryNewPromotionAndCapstoneAConcreteIdentityMechanic()
    {
        var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();
        var monsterCatalog = new MonsterCombatCatalog(Options.Create(
            config.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!));
        var catalog = new SkillCatalog(Options.Create(
            config.GetSection(SkillOptions.SectionName).Get<SkillOptions>()!), monsterCatalog);

        var priest = catalog.FindSkill("priest-group-heal")!;
        Assert.Contains(SkillCatalog.EffectsFor(priest), effect => effect.Type == "Cleanse");
        var arcanist = catalog.FindSkill("arcanist-overcharge")!;
        Assert.Contains(SkillCatalog.EffectsFor(arcanist), effect => effect.Type == "CooldownReduction" && effect.Power == 1);
        var marksman = catalog.FindSkill("marksman-sniper-shot")!;
        Assert.Equal("hunters-mark", marksman.RequiredTargetStatusCode);
        Assert.Equal(25, marksman.ConditionalDamageBonusPercent);
        var beastmaster = catalog.FindSkill("beastmaster-coordinated-assault")!;
        Assert.Contains(SkillCatalog.EffectsFor(beastmaster), effect => effect.Type == "Guard" && effect.Target == "FrontAlly");
        var assassin = catalog.FindSkill("assassin-deathblow")!;
        Assert.Equal(35, assassin.TargetHpBelowPercent);
        Assert.Equal(30, assassin.ConditionalDamageBonusPercent);

        var mechanicalCapstones = new[]
        {
            "mage-arcane-mastery", "mage-stable-channeling", "mage-frozen-heart",
            "hunter-predator", "hunter-relentless", "hunter-hardened",
            "rogue-relentless-assault", "rogue-opportunist", "rogue-escape-artist"
        };
        Assert.All(mechanicalCapstones, code =>
        {
            var node = catalog.FindTalentNode(code)!;
            Assert.Null(node.EffectCode);
            Assert.Equal(0, node.ValuePerRank);
        });
        Assert.NotNull(monsterCatalog.FindStatus("hunter-resilience"));
        Assert.NotNull(monsterCatalog.FindStatus("rogue-opening"));
    }

    [Theory]
    [InlineData("swordsman", "knight", "warrior")]
    [InlineData("acolyte", "inquisitor", "priest")]
    [InlineData("mage", "arcanist", "elementalist")]
    [InlineData("hunter", "beastmaster", "marksman")]
    [InlineData("rogue", "assassin", "trickster")]
    public async Task EveryBaseProfessionAdvertisesTwoPromotionPathsAtLevelTen(
        string professionCode, string firstPromotion, string secondPromotion)
    {
        await using var test = await FormalTreeContext.CreateAsync(professionCode, level: 10, points: 9);

        var response = (await test.Service.GetAsync("token", 1)).Response!;

        Assert.True(response.CanPromote);
        Assert.Equal(new[] { firstPromotion, secondPromotion }, response.PromotionOptions.Select(option => option.Code).Order());
        Assert.All(response.PromotionOptions, option => Assert.False(string.IsNullOrWhiteSpace(option.GrantedSkillName)));
    }

    [Theory]
    [InlineData("swordsman", "knight", "knight-guard")]
    [InlineData("swordsman", "warrior", "warrior-fury")]
    [InlineData("acolyte", "priest", "priest-group-heal")]
    [InlineData("acolyte", "inquisitor", "inquisitor-condemn")]
    [InlineData("mage", "elementalist", "elementalist-pyroblast")]
    [InlineData("mage", "arcanist", "arcanist-overcharge")]
    [InlineData("hunter", "marksman", "marksman-sniper-shot")]
    [InlineData("hunter", "beastmaster", "beastmaster-coordinated-assault")]
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

    private static SkillCatalog LoadProductionCatalog()
    {
        var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();
        var monsters = new MonsterCombatCatalog(Options.Create(
            config.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!));
        return new SkillCatalog(Options.Create(config.GetSection(SkillOptions.SectionName).Get<SkillOptions>()!), monsters);
    }

    private static List<Dictionary<string, int>> EnumerateLevelTenBuilds(IReadOnlyList<SkillTalentNodeOptions> nodes)
    {
        var states = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal) { [string.Empty] = [] };
        for (var spent = 0; spent < 9; spent++)
        {
            var next = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            foreach (var ranks in states.Values)
            foreach (var node in nodes)
            {
                var rank = ranks.GetValueOrDefault(node.Code);
                if (rank >= node.MaxRank || 10 < node.RequiredLevel + rank || spent < node.RequiredTreePoints) continue;
                if (node.Prerequisites.Any(code => ranks.GetValueOrDefault(code) < nodes.Single(parent => parent.Code == code).MaxRank)) continue;
                if (node.AnyPrerequisites.Count > 0 && !node.AnyPrerequisites.Any(code =>
                        ranks.GetValueOrDefault(code) >= nodes.Single(parent => parent.Code == code).MaxRank)) continue;
                if (node.ExclusiveGroup is not null && nodes.Any(other => other.Code != node.Code &&
                        string.Equals(other.ExclusiveGroup, node.ExclusiveGroup, StringComparison.OrdinalIgnoreCase) &&
                        ranks.GetValueOrDefault(other.Code) > 0)) continue;

                var added = new Dictionary<string, int>(ranks, StringComparer.OrdinalIgnoreCase) { [node.Code] = rank + 1 };
                var key = string.Join('|', nodes.Select(candidate => added.GetValueOrDefault(candidate.Code)));
                next.TryAdd(key, added);
            }
            states = next;
        }
        return states.Values.ToList();
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
