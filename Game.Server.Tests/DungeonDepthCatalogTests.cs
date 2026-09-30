using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class DungeonDepthCatalogTests
{
    private static readonly string[] Codes = ["kobold-mine-depths", "plague-crypt-depths", "ragefire-heart",
        "frostspring-throne", "windfury-spire", "dawn-core"];

    [Fact]
    public void DepthUsesExplicitCodeAndRoundsOnlyFinalStat()
    {
        var catalog = CreateDepths();
        Assert.True(catalog.ValidateDepth(Codes[0].ToUpperInvariant(), 10));
        Assert.False(catalog.ValidateDepth(Codes[0], 11));
        Assert.False(catalog.ValidateDepth(Codes[0], 0));
        Assert.True(catalog.ValidateDepth("kobold-mine", 1));
        Assert.False(catalog.ValidateDepth("kobold-mine", 2));
        Assert.False(catalog.ValidateDepth("名字·深层LV1", 2));
        Assert.Equal(1.331m, catalog.StatMultiplier(Codes[0], 4));
        Assert.Equal(2, catalog.ScaleStat(1, 4, Codes[0]));
        Assert.Equal(134, catalog.ScaleStat(100, 4, Codes[0]));
        Assert.Equal(0, catalog.ScaleStat(0, 10, Codes[0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.ScaleStat(100, 2, "kobold-mine"));
        Assert.Equal("烛井矿窟·深层LV7", catalog.DisplayName("烛井矿窟·深层LV1", 7));
        Assert.Equal("新副本·深层LV2", catalog.DisplayName("新副本", 2));
        Assert.Equal(catalog.AddedMechanics(Codes[0], 4), catalog.AddedMechanics(Codes[0], 10));
    }

    [Fact]
    public void ProductionConfigurationScalesAllSixEncountersAndKeepsLv4Mechanics()
    {
        var configuration = Configuration();
        var depths = CreateDepths(configuration);
        var combat = new MonsterCombatCatalog(Options.Create(Bind<MonsterCombatOptions>(configuration, MonsterCombatOptions.SectionName)));
        var encounters = new DungeonEncounterCatalog(Options.Create(Bind<DungeonEncounterOptions>(configuration,
            DungeonEncounterOptions.SectionName)), combat, depthCatalog: depths);
        foreach (var code in Codes)
        {
            var definition = depths.Find(code)!;
            Assert.Equal(1, definition.Stage);
            Assert.Equal(10m, definition.GoldBonusPercent);
            Assert.Equal(10m, definition.KillExtraRollChancePercent);
            Assert.Equal(10m, definition.ClearExtraRollChancePercent);
            Assert.Equal("weapon-breakthrough-fragment-t1", definition.ChallengeFragmentCode);
            var dungeon = new Dungeon { Code = code };
            var baseMonsters = encounters.CreateMonsters(dungeon);
            Assert.Equal(5, baseMonsters.Count);
            Assert.Equal(3, encounters.GetWaveCount(dungeon));
            for (var depth = 2; depth <= 10; depth++)
            {
                var monsters = encounters.CreateMonsters(dungeon, depth);
                for (var index = 0; index < monsters.Count; index++)
                {
                    var monster = monsters[index];
                    var original = baseMonsters[index];
                    Assert.Equal(depths.ScaleStat(original.MaxHp, depth, code), monster.MaxHp);
                    Assert.Equal(monster.MaxHp, monster.Hp);
                    Assert.Equal(monster.MaxHp, monster.BaseMaxHp);
                    Assert.Equal(depths.ScaleStat(original.Attack, depth, code), monster.Attack);
                    Assert.Equal(original.Defense, monster.Defense);
                    Assert.Equal(original.RewardProfileCode, monster.RewardProfileCode);
                    Assert.Equal((original.WaveNumber, original.Position), (monster.WaveNumber, monster.Position));
                    if (!monster.IsBoss)
                    {
                        Assert.Equal(original.CombatProfileCode, monster.CombatProfileCode);
                        continue;
                    }
                    var profile = combat.FindProfile(monster.CombatProfileCode)!;
                    var originalProfile = combat.FindProfile(original.CombatProfileCode)!;
                    Assert.Equal(originalProfile.Skills.Count + Math.Min(depth, 4) - 1, profile.Skills.Count);
                    Assert.All(originalProfile.Skills, skill => Assert.Contains(profile.Skills, entry => entry.Code == skill.Code));
                    var placeholders = profile.Skills.Where(skill => skill.Code.StartsWith("depth-placeholder-")).ToList();
                    Assert.Equal(Math.Min(depth, 4) - 1, placeholders.Count);
                    Assert.All(placeholders, entry =>
                    {
                        var skill = combat.FindSkill(entry.Code)!;
                        Assert.Equal(101, skill.DamagePowerPercent);
                        Assert.Contains("尚未实现", skill.Description);
                    });
                    Assert.Equal(placeholders.Count, placeholders.Select(entry => combat.FindSkill(entry.Code)!.Name).Distinct().Count());
                    if (depth >= 5)
                        Assert.Equal(encounters.CreateMonsters(dungeon, 4)[index].CombatProfileCode, monster.CombatProfileCode);
                }
            }
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => encounters.CreateMonsters(new Dungeon { Code = "kobold-mine" }, 2));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(5, 3)]
    public async Task LoadedPlaceholdersActuallyExecuteAndCycleWithCooldown(int depth, int expectedCount)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var combat = new MonsterCombatCatalog(Options.Create(new MonsterCombatOptions
        {
            Profiles = new() { ["base"] = new() { SkillUseChancePercent = 0 } }
        }));
        var room = new Room { Id = 1, DungeonId = 1, MonsterId = 1, OwnerUserId = 1, SlotCount = 1 };
        var monster = new Monster { Id = 1, RoomId = 1, Name = "Boss", Hp = 100, MaxHp = 100, Attack = 10,
            CombatProfileCode = combat.ResolveDepthProfile("base", depth) };
        var character = new Character { Id = 1, UserId = 1, Name = "Player", Hp = 1000, MaxHp = 1000 };
        var slot = new RoomSlot { Id = 1, RoomId = 1, UserId = 1, CharacterId = 1, SlotIndex = 1 };
        db.AddRange(room, monster, character, slot);
        await db.SaveChangesAsync();
        var service = new MonsterCombatService(db, combat);
        for (var index = 0; index < expectedCount; index++)
        {
            room.RoundNumber = index;
            var intent = await service.EnsureIntentAsync(room, monster);
            Assert.Equal($"depth-placeholder-lv{index + 2}", intent.SkillCode);
            var previousHp = character.Hp;
            var logs = new List<string>();
            await service.ExecuteIntentAsync(room, monster, [new(slot, character)], new Dictionary<int, Game.Shared.Enums.ElementType>(), logs);
            await db.SaveChangesAsync();
            Assert.True(character.Hp < previousHp);
            Assert.Contains(logs, log => log.Contains(combat.FindSkill(intent.SkillCode)!.Name));
        }
        room.RoundNumber = 4;
        Assert.Equal("depth-placeholder-lv2", (await service.EnsureIntentAsync(room, monster)).SkillCode);
    }

    [Fact]
    public void InvalidConfigurationIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions
        {
            Dungeons = new() { ["bad"] = new() { ChallengeFragmentCode = "fragment", KillExtraRollChancePercent = 101 } }
        })));
    }

    [Fact]
    public void MaximumDepthCannotCreateAnUnboundedPreviewList()
    {
        Assert.Throws<InvalidOperationException>(() => new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions
        {
            Dungeons = new() { ["unbounded"] = new() { ChallengeFragmentCode = "fragment", MaximumDepth = int.MaxValue, GrowthPercent = 0 } }
        })));
    }

    [Fact]
    public void UnsafeMaximumMultiplierIsRejectedDuringConfigurationLoading()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions
        {
            Dungeons = new() { ["overflow"] = new() { ChallengeFragmentCode = "fragment", MaximumDepth = 100, GrowthPercent = 100 } }
        })));
        Assert.Contains("overflow", exception.Message);
        Assert.Contains("LV100", exception.Message);
        Assert.IsType<OverflowException>(exception.InnerException);
    }

    [Theory]
    [InlineData(int.MaxValue, 1)]
    [InlineData(1, int.MaxValue)]
    public void UnsafeMonsterStatIsRejectedWhenEncounterCatalogLoads(int hp, int attack)
    {
        var depths = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions
        {
            Dungeons = new() { ["overflow"] = new() { ChallengeFragmentCode = "fragment", MaximumDepth = 2 } }
        }));
        var exception = Assert.Throws<InvalidOperationException>(() => new DungeonEncounterCatalog(Options.Create(new DungeonEncounterOptions
        {
            Dungeons = new() { ["overflow"] = [new() { Monsters = [new() { Name = "Huge", MaxHp = hp, Attack = attack }] }] }
        }), depthCatalog: depths));
        Assert.Contains("overflow", exception.Message);
        Assert.Contains("Huge", exception.Message);
        Assert.Contains("LV2", exception.Message);
        Assert.IsType<OverflowException>(exception.InnerException);
    }

    [Fact]
    public void DepthConfigurationCannotBypassStatValidationThroughLegacyEncounterFallback()
    {
        var depths = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions
        {
            Dungeons = new() { ["missing"] = new() { ChallengeFragmentCode = "fragment" } }
        }));
        var exception = Assert.Throws<InvalidOperationException>(() => new DungeonEncounterCatalog(
            Options.Create(new DungeonEncounterOptions()), depthCatalog: depths));
        Assert.Contains("missing", exception.Message);
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();

    private static T Bind<T>(IConfiguration configuration, string section) where T : new()
    {
        var options = new T();
        configuration.GetSection(section).Bind(options);
        return options;
    }

    private static DungeonDepthCatalog CreateDepths(IConfiguration? configuration = null) =>
        new(Options.Create(Bind<DungeonDepthOptions>(configuration ?? Configuration(), DungeonDepthOptions.SectionName)));
}
