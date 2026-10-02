using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class DungeonDefinitionConfigurationTests
{
    [Fact]
    public void ExplicitDepthStatsPreserveLv1AndScaleFromTheLatestAuthoredDepth()
    {
        var options = EncounterOptions();
        options.Dungeons["configured"][1].Monsters[0].DepthStats = [new() { Depth = 4, MaxHp = 1300, Attack = 33 }];
        var catalog = new DungeonEncounterCatalog(Options.Create(options),
            new MonsterCombatCatalog(Options.Create(CombatOptions())), depthCatalog: Depths());
        Assert.Equal((500, 20), Stats(1));
        Assert.Equal((605, 25), Stats(3));
        Assert.Equal((1300, 33), Stats(4));
        Assert.Equal((1430, 37), Stats(5));
        (int, int) Stats(int depth)
        {
            var monster = catalog.GetRepresentativeMonster(Dungeon(), depth);
            return (monster.MaxHp, monster.Attack);
        }
    }

    [Theory]
    [InlineData(1, 100, 20)]
    [InlineData(6, 100, 20)]
    [InlineData(4, 0, 20)]
    [InlineData(4, 100, -1)]
    [InlineData(4, int.MaxValue, 20)]
    public void InvalidExplicitDepthStatsCannotReachCombat(int depth, int hp, int attack)
    {
        var options = EncounterOptions();
        options.Dungeons["configured"][1].Monsters[0].DepthStats = [new() { Depth = depth, MaxHp = hp, Attack = attack }];
        Assert.Throws<InvalidOperationException>(() => new DungeonEncounterCatalog(Options.Create(options),
            new MonsterCombatCatalog(Options.Create(CombatOptions())), depthCatalog: Depths()));
    }

    [Fact]
    public void DuplicateExplicitDepthStatsAreRejected()
    {
        var options = EncounterOptions();
        options.Dungeons["configured"][1].Monsters[0].DepthStats =
            [new() { Depth = 4, MaxHp = 100, Attack = 20 }, new() { Depth = 4, MaxHp = 200, Attack = 30 }];
        Assert.Throws<InvalidOperationException>(() => new DungeonEncounterCatalog(Options.Create(options),
            new MonsterCombatCatalog(Options.Create(CombatOptions())), depthCatalog: Depths()));
    }

    [Fact]
    public void Lv1OnlyCatalogCanReadProductionOptionsContainingHigherDepthStats()
    {
        var options = EncounterOptions();
        options.Dungeons["configured"][1].Monsters[0].DepthStats = [new() { Depth = 4, MaxHp = 1300, Attack = 33 }];
        var catalog = new DungeonEncounterCatalog(Options.Create(options), new MonsterCombatCatalog(Options.Create(CombatOptions())));
        Assert.Equal(500, catalog.GetRepresentativeMonster(Dungeon()).MaxHp);
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.GetRepresentativeMonster(Dungeon(), 4));
    }

    [Fact]
    public void DifferentProfilesUseDeclaredStagesReplacementAndLastStageBeyondItsDepth()
    {
        var catalog = new MonsterCombatCatalog(Options.Create(CombatOptions()));
        Assert.Equal("boss-a", catalog.ResolveDepthProfile("boss-a", 1));
        Assert.Equal("boss-a:depth-lv2", catalog.ResolveDepthProfile("boss-a", 3));
        Assert.Equal("boss-a:depth-lv4", catalog.ResolveDepthProfile("boss-a", 10));
        Assert.Equal(new[] { "base", "burst" }, Codes(catalog, "boss-a", 2));
        Assert.Equal(new[] { "phase", "late" }, Codes(catalog, "boss-a", 4));
        Assert.Equal(0, catalog.ResolveProfile(catalog.ResolveDepthProfile("boss-a", 4))!.SkillUseChancePercent);
        Assert.Equal("boss-b", catalog.ResolveDepthProfile("boss-b", 2));
        Assert.Equal("boss-b:depth-lv3", catalog.ResolveDepthProfile("boss-b", 10));
        Assert.Equal(new[] { "base", "late" }, Codes(catalog, "boss-b", 3));
        Assert.Equal(new[] { "phase", "late" }, catalog.GetAddedMechanics("boss-a", 5));
        Assert.Equal(new[] { "late" }, catalog.GetAddedMechanics("boss-b", 3));
    }

    [Fact]
    public void ExportIsDetachedAuthoredConfigurationAndRebuildsIdenticalGeneratedProfiles()
    {
        var source = CombatOptions();
        var catalog = new MonsterCombatCatalog(Options.Create(source));
        source.DepthProgressions["alpha"].Clear();
        catalog.FindSkill("base")!.DamagePowerPercent = 999;
        var exported = catalog.ExportOptions();
        Assert.Equal(3, exported.Profiles.Count);
        Assert.DoesNotContain(exported.Profiles.Keys, code => code.Contains(":depth-lv"));
        Assert.Equal(10, exported.Skills.Single(skill => skill.Code == "base").DamagePowerPercent);
        var rebuilt = new MonsterCombatCatalog(Options.Create(exported));
        for (var depth = 1; depth <= 10; depth++)
        {
            Assert.Equal(catalog.ResolveDepthProfile("boss-a", depth), rebuilt.ResolveDepthProfile("boss-a", depth));
            Assert.Equal(Codes(catalog, "boss-a", depth), Codes(rebuilt, "boss-a", depth));
        }
        exported.Skills.Clear();
        exported.DepthProgressions["alpha"][0].AddedSkills.Clear();
        Assert.Equal(4, catalog.ExportOptions().Skills.Count);
        Assert.Single(catalog.ExportOptions().DepthProgressions["alpha"][0].AddedSkills);
    }

    [Theory]
    [InlineData("missing-progression")]
    [InlineData("missing-skill")]
    [InlineData("missing-replacement")]
    [InlineData("duplicate-depth")]
    [InlineData("duplicate-skill")]
    public void InvalidStageReferencesCannotReachRuntime(string fault)
    {
        var options = CombatOptions();
        switch (fault)
        {
            case "missing-progression": options.Profiles["boss-a"].DepthProgressionCode = "missing"; break;
            case "missing-skill": options.DepthProgressions["alpha"][0].AddedSkills[0].Code = "missing"; break;
            case "missing-replacement": options.DepthProgressions["alpha"][1].ReplacementProfileCode = "missing"; break;
            case "duplicate-depth": options.DepthProgressions["alpha"][1].Depth = 2; break;
            case "duplicate-skill": options.DepthProgressions["alpha"][0].AddedSkills[0].Code = "base"; break;
        }
        Assert.Throws<InvalidOperationException>(() => new MonsterCombatCatalog(Options.Create(options)));
    }

    [Fact]
    public void RepresentativeSummaryAndAddedMechanicsComeFromActualBossWithoutMutatingInput()
    {
        var depths = Depths();
        var combat = new MonsterCombatCatalog(Options.Create(CombatOptions()));
        var encounters = new DungeonEncounterCatalog(Options.Create(EncounterOptions()), combat, depthCatalog: depths);
        var dungeon = Dungeon();
        var representative = encounters.GetRepresentativeMonster(dungeon, 2);
        Assert.Equal(("Boss", 550, 22, 3),
            (representative.Name, representative.MaxHp, representative.Attack, representative.Defense));
        Assert.Equal(("stale", 777, 77), (dungeon.MonsterName, dungeon.MonsterMaxHp, dungeon.MonsterAttack));
        Assert.Equal(new[] { "burst" }, encounters.GetAddedMechanics(dungeon, 2));
        Assert.Equal(new[] { "phase", "late" }, encounters.GetAddedMechanics(dungeon, 5));
        var world = new WorldCatalog(Options.Create(new WorldOptions
        {
            Regions = [new RegionOptions { Code = "region", Name = "Region", Description = "region", FeaturedElement = ElementType.Wind,
                FeaturedDungeonCode = "configured", FeaturedWeaponCode = "weapon" }], Dungeons = [dungeon]
        }), encounters: encounters);
        Assert.Equal(("Boss", 500, 20, 3), (world.Dungeons[0].MonsterName, world.Dungeons[0].MonsterMaxHp,
            world.Dungeons[0].MonsterAttack, world.Dungeons[0].MonsterDefense));
        var legacy = Dungeon();
        legacy.Code = "unconfigured";
        Assert.Equal(("stale", 777, 77), (encounters.GetRepresentativeMonster(legacy).Name,
            encounters.GetRepresentativeMonster(legacy).MaxHp, encounters.GetRepresentativeMonster(legacy).Attack));
    }

    [Fact]
    public async Task PublicPreviewAndDatabaseSynchronizationUseEncounterRatherThanStaleStoredSummary()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options;
        await using var db = new GameDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var encounters = new DungeonEncounterCatalog(Options.Create(EncounterOptions()),
            new MonsterCombatCatalog(Options.Create(CombatOptions())), depthCatalog: Depths());
        var world = new WorldCatalog(Options.Create(new WorldOptions
        {
            Regions = [new RegionOptions { Code = "region", Name = "Region", Description = "region", FeaturedElement = ElementType.Wind,
                FeaturedDungeonCode = "configured", FeaturedWeaponCode = "weapon" }], Dungeons = [Dungeon()]
        }), encounters: encounters);
        await DbInitializer.EnsureDefaultDungeonsAsync(db, world, encounters);
        var stored = await db.Dungeons.SingleAsync();
        Assert.Equal((500, 20, 3), (stored.MonsterMaxHp, stored.MonsterAttack, stored.MonsterDefense));
        stored.MonsterMaxHp = 1;
        stored.MonsterAttack = 1;
        stored.MonsterName = "outdated";
        await db.SaveChangesAsync();
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        var rooms = new RoomService(db, new UserService(db, progression, skills), progression,
            ConsumableTestFactory.Create(), skills, RewardTestFactory.CreateService(db, progression),
            encounterCatalog: encounters, depthCatalog: Depths());
        var preview = await rooms.GetDungeonAsync(stored.Id, null, 2);
        Assert.NotNull(preview);
        Assert.Equal(("Boss", 550, 22), (preview.MonsterName, preview.MonsterMaxHp, preview.MonsterAttack));
        Assert.Equal(550, preview.Monsters[^1].MaxHp);
        Assert.Equal(new[] { "burst" }, preview.Depths.Single(depth => depth.DepthLevel == 2).AddedMechanics);
    }

    [Fact]
    public async Task PublicPreviewShowsDefaultPlaceholderFlagAndAllowsCalibratedDepthsToDisableIt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var dungeon = Dungeon();
        db.Dungeons.Add(dungeon);
        await db.SaveChangesAsync();
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();

        RoomService Rooms(DungeonDepthCatalog depths) => new(db, new UserService(db, progression, skills), progression,
            ConsumableTestFactory.Create(), skills, RewardTestFactory.CreateService(db, progression), depthCatalog: depths);

        var defaultPreview = await Rooms(Depths()).GetDungeonAsync(dungeon.Id, null);
        Assert.NotNull(defaultPreview);
        Assert.True(defaultPreview.UsesPlaceholderBalance);
        var calibrated = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions
        {
            Dungeons = new() { ["configured"] = new() { UsesPlaceholderBalance = false, ChallengeFragmentCode = "fragment" } }
        }));
        var calibratedPreview = await Rooms(calibrated).GetDungeonAsync(dungeon.Id, null);
        Assert.NotNull(calibratedPreview);
        Assert.False(calibratedPreview.UsesPlaceholderBalance);
    }

    [Fact]
    public void CombinedDepthAndPartyHpOverflowIsRejectedEvenWhenBothInputsAreIndividuallyValid()
    {
        var depths = Depths();
        var options = EncounterOptions();
        options.Dungeons["configured"][1].Monsters[0].MaxHp = 600_000_000;
        var encounters = new DungeonEncounterCatalog(Options.Create(options),
            new MonsterCombatCatalog(Options.Create(CombatOptions())), depthCatalog: depths);
        Assert.True(encounters.GetRepresentativeMonster(Dungeon(), 5).MaxHp < int.MaxValue);
        var exception = Assert.Throws<InvalidOperationException>(() => DungeonContentValidator.ValidateCombinedStats(
            Dungeon(), encounters, depths, PartyScalingCatalog.Default));
        Assert.Contains("configured", exception.Message);
        Assert.Contains("5 characters", exception.Message);
    }

    [Theory]
    [InlineData("configured", 1)]
    [InlineData("missing", 0)]
    [InlineData("configured", 999)]
    public void EligibilityIsExplicitAndUnknownOrInvalidPolicyIsRejected(string code, int value)
    {
        var options = EncounterOptions();
        options.RewardEligibility[code] = (DungeonRewardEligibility)value;
        if (code == "configured" && Enum.IsDefined((DungeonRewardEligibility)value))
        {
            var catalog = new DungeonEncounterCatalog(Options.Create(options), new MonsterCombatCatalog(Options.Create(CombatOptions())));
            Assert.Equal((DungeonRewardEligibility)value, catalog.ResolveRewardEligibility(code, DungeonRewardEligibility.CurrentSlots));
            Assert.Equal(DungeonRewardEligibility.CurrentSlots,
                catalog.ResolveRewardEligibility("legacy", DungeonRewardEligibility.CurrentSlots));
        }
        else Assert.Throws<InvalidOperationException>(() => new DungeonEncounterCatalog(Options.Create(options),
            new MonsterCombatCatalog(Options.Create(CombatOptions()))));
    }

    [Fact]
    public void ProductionDefinitionsKeepProfileCodesAndDeriveMetadataWithoutWorldStatCopies()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();
        var combat = new MonsterCombatCatalog(Options.Create(configuration.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!));
        var depthOptions = configuration.GetSection(DungeonDepthOptions.SectionName).Get<DungeonDepthOptions>()!;
        var encounters = new DungeonEncounterCatalog(Options.Create(configuration.GetSection(DungeonEncounterOptions.SectionName)
            .Get<DungeonEncounterOptions>()!), combat, depthCatalog: new DungeonDepthCatalog(Options.Create(depthOptions)));
        var world = WorldCatalog.LoadDefault();
        var legacy = world.Dungeons.Single(dungeon => dungeon.Code == "slime-field");
        Assert.Equal(("Slime", 50, 8, 2),
            (legacy.MonsterName, legacy.MonsterMaxHp, legacy.MonsterAttack, legacy.MonsterDefense));
        encounters.PopulateRepresentativeStats(legacy);
        Assert.Equal("Slime", legacy.MonsterName);
        foreach (var dungeon in world.Dungeons.Where(dungeon => dungeon.IsVisible))
        {
            var representative = encounters.GetRepresentativeMonster(dungeon);
            Assert.Equal((representative.Name, representative.MaxHp, representative.Attack, representative.Defense),
                (dungeon.MonsterName, dungeon.MonsterMaxHp, dungeon.MonsterAttack, dungeon.MonsterDefense));
            if (!depthOptions.Dungeons.ContainsKey(dungeon.Code)) continue;
            var boss = representative.CombatProfileCode;
            Assert.Equal($"{boss}:depth-lv4", combat.ResolveDepthProfile(boss, 10));
            Assert.Equal(encounters.GetAddedMechanics(dungeon, 4), encounters.GetAddedMechanics(dungeon, 10));
            Assert.Equal(dungeon.Code == "plague-crypt-depths" ? 4 : 3,
                encounters.GetAddedMechanics(dungeon, 4).Count);
            Assert.Equal(DungeonRewardEligibility.ActualParticipants,
                encounters.ResolveRewardEligibility(dungeon.Code, DungeonRewardEligibility.CurrentSlots));
        }
    }

    private static string[] Codes(MonsterCombatCatalog catalog, string code, int depth) =>
        catalog.ResolveProfile(catalog.ResolveDepthProfile(code, depth))!.Skills.Select(skill => skill.Code).ToArray();

    private static MonsterCombatOptions CombatOptions() => new()
    {
        Skills = new[] { "base", "burst", "phase", "late" }.Select(code => new MonsterSkillOptions
            { Code = code, Name = code, Description = code, DamagePowerPercent = 10 }).ToList(),
        Profiles = new()
        {
            ["boss-a"] = new() { SkillUseChancePercent = 50, Skills = [new() { Code = "base" }], DepthProgressionCode = "alpha" },
            ["boss-b"] = new() { SkillUseChancePercent = 80, Skills = [new() { Code = "base" }], DepthProgressionCode = "beta" },
            ["replacement"] = new() { SkillUseChancePercent = 0, Skills = [new() { Code = "phase" }] }
        },
        DepthProgressions = new()
        {
            ["alpha"] = [new() { Depth = 2, AddedSkills = [new() { Code = "burst" }] },
                new() { Depth = 4, ReplacementProfileCode = "replacement", AddedSkills = [new() { Code = "late" }] }],
            ["beta"] = [new() { Depth = 3, AddedSkills = [new() { Code = "late", Weight = 7 }] }]
        }
    };

    private static DungeonDepthCatalog Depths() => new(Options.Create(new DungeonDepthOptions
    {
        Dungeons = new() { ["configured"] = new() { MaximumDepth = 5, GrowthPercent = 10, ChallengeFragmentCode = "fragment" } }
    }));

    private static DungeonEncounterOptions EncounterOptions() => new()
    {
        Dungeons = new()
        {
            ["configured"] = [new() { Monsters = [new() { Name = "Guard", MaxHp = 100, Attack = 10 }] },
                new() { Monsters = [new() { Name = "Boss", MaxHp = 500, Attack = 20, Defense = 3, IsBoss = true, CombatProfileCode = "boss-a" }] }]
        }
    };

    private static Dungeon Dungeon() => new()
    {
        Code = "configured", Name = "Configured", RegionCode = "region", DungeonKind = "Dungeon",
        MonsterName = "stale", MonsterMaxHp = 777, MonsterAttack = 77, SlotCount = 5, PartyScalingProfileCode = "hunt-hp"
    };
}
