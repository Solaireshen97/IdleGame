using System.Text.Json;
using System.Text.Json.Nodes;
using Game.BalanceSimulator;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Game.BalanceSimulator.Tests;

public sealed class SimulatorScenarioTests : IDisposable
{
    private readonly string _previousDirectory = Directory.GetCurrentDirectory();
    private readonly string _fixtureDirectory;
    private readonly string _config;
    private readonly string _world;

    public SimulatorScenarioTests()
    {
        Directory.SetCurrentDirectory(Game.Server.Tests.TestRepository.Root);
        _fixtureDirectory = Path.Combine(Path.GetTempPath(), "idle-balance-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_fixtureDirectory);
        _config = Path.Combine(_fixtureDirectory, "settings.json");
        _world = Path.Combine(_fixtureDirectory, "world.json");
        var settings = JsonNode.Parse(File.ReadAllText("Game.Server/appsettings.json"))!;
        // Fixed-value scenario fixtures declare their historical policy explicitly.
        settings["CombatDamage"] = new JsonObject { ["VariancePercent"] = 0 };
        var world = JsonNode.Parse(File.ReadAllText("Game.Server/world.json"))!;
        var candidates = world["World"]!["Dungeons"]!.AsArray();
        var template = candidates.First(value => value!["Code"]!.GetValue<string>() == "kobold-mine")!;
        var dungeon = template.DeepClone();
        dungeon["Code"] = "simulator-isolated-boss";
        dungeon["Name"] = "Isolated simulator boss";
        dungeon["MonsterMaxHp"] = 100;
        dungeon["MonsterAttack"] = 0;
        dungeon["MonsterDefense"] = 0;
        dungeon["PartyScalingProfileCode"] = "fixed";
        candidates.Add(dungeon);
        var regionCode = template["RegionCode"]!.GetValue<string>();
        var element = world["World"]!["Regions"]!.AsArray().Single(region => region!["Code"]!.GetValue<string>() == regionCode)!["FeaturedElement"]!.DeepClone();
        settings["DungeonEncounters"]!["Dungeons"]!["simulator-isolated-boss"] = new JsonArray(new JsonObject
        {
            ["Monsters"] = new JsonArray(new JsonObject
            {
                ["Name"] = "Isolated simulator boss", ["MaxHp"] = 100, ["Attack"] = 0, ["Defense"] = 0,
                ["Element"] = element, ["IsBoss"] = true, ["CombatProfileCode"] = "simulator-boss",
                ["RewardProfileCode"] = "simulator-isolated-boss"
            })
        });
        var combat = settings["MonsterCombat"]!;
        foreach (var code in new[] { "simulator-base", "simulator-layer-four", "simulator-layer-ten" })
            combat["Skills"]!.AsArray().Add(new JsonObject
            {
                ["Code"] = code, ["Name"] = code, ["Description"] = "Scenario test only", ["TargetType"] = "Front",
                ["Effects"] = new JsonArray(new JsonObject { ["Type"] = "Damage", ["Target"] = "Front", ["Power"] = 1 }),
                ["CooldownRounds"] = 1
            });
        combat["Profiles"]!["simulator-boss"] = new JsonObject
        {
            ["SkillUseChancePercent"] = 100, ["DepthProgressionCode"] = "simulator-layers",
            ["Skills"] = new JsonArray(new JsonObject { ["Code"] = "simulator-base", ["Weight"] = 1 })
        };
        combat["DepthProgressions"] ??= new JsonObject();
        combat["DepthProgressions"]!["simulator-layers"] = new JsonArray(
            new JsonObject { ["Depth"] = 4, ["AddedSkills"] = new JsonArray(new JsonObject { ["Code"] = "simulator-layer-four", ["Weight"] = 1 }) },
            new JsonObject { ["Depth"] = 10, ["AddedSkills"] = new JsonArray(new JsonObject { ["Code"] = "simulator-layer-ten", ["Weight"] = 1 }) });
        settings["DungeonDepths"]!["Dungeons"]!["simulator-isolated-boss"] = new JsonObject
        {
            ["MaximumDepth"] = 10, ["GrowthPercent"] = 10, ["ChallengeFragmentCode"] = "weapon-breakthrough-fragment-t1",
            ["ChallengeFragmentChancePercent"] = 0, ["KillExtraRollChancePercent"] = 0, ["ClearExtraRollChancePercent"] = 0
        };
        settings["Rewards"]!["DungeonClears"]!["simulator-isolated-boss"] = new JsonObject
        {
            ["Gold"] = 1000, ["Experience"] = 0, ["Drops"] = new JsonArray()
        };
        settings["Rewards"]!["MonsterKills"]!["simulator-isolated-boss"] = new JsonObject
        {
            ["Gold"] = 0, ["Experience"] = 0, ["Drops"] = new JsonArray()
        };
        File.WriteAllText(_config, settings.ToJsonString());
        File.WriteAllText(_world, world.ToJsonString());
    }

    [Theory]
    [InlineData(1, 100, "simulator-boss")]
    [InlineData(4, 134, "simulator-boss:depth-lv4")]
    [InlineData(10, 236, "simulator-boss:depth-lv10")]
    public async Task ArbitraryDungeonUsesProductionDepthStatsProfilesAndReproducibleTrace(int depth, int hp, string profile)
    {
        var arguments = Arguments(depth);
        using var first = JsonDocument.Parse(await BalanceSimulatorApplication.RunAsync(arguments, writeReport: false));
        using var second = JsonDocument.Parse(await BalanceSimulatorApplication.RunAsync(arguments, writeReport: false));
        var sample = Sample(first);
        var repeated = Sample(second);
        Assert.Equal("simulator-isolated-boss", sample.GetProperty("Dungeon").GetString());
        Assert.Equal(depth, sample.GetProperty("Depth").GetInt32());
        Assert.Equal(hp, sample.GetProperty("InitialMonsters")[0].GetProperty("MaxHp").GetInt32());
        Assert.Equal(profile, sample.GetProperty("InitialMonsters")[0].GetProperty("CombatProfileCode").GetString());
        Assert.Equal(64, sample.GetProperty("RuleRevision").GetString()!.Length);
        Assert.Equal(sample.GetProperty("RuleRevision").GetString(), repeated.GetProperty("RuleRevision").GetString());
        Assert.Equal(sample.GetProperty("BattleFingerprint").GetString(), repeated.GetProperty("BattleFingerprint").GetString());
        Assert.Equal(sample.GetProperty("RoundTrace").GetRawText(), repeated.GetProperty("RoundTrace").GetRawText());
        Assert.True(sample.GetProperty("Victory").GetBoolean());
        Assert.Equal("None", sample.GetProperty("FailureCategory").GetString());
        Assert.Equal(0, sample.GetProperty("StartingPotions").GetInt32());
    }

    [Fact]
    public async Task StartingMasteryUsesProductionRewardBonusesAndIsReported()
    {
        using var baseReport = JsonDocument.Parse(await BalanceSimulatorApplication.RunAsync(Arguments(4), writeReport: false));
        using var mastered = JsonDocument.Parse(await BalanceSimulatorApplication.RunAsync(Arguments(4).Concat(new[] { "--mastery", "2" }).ToArray(), writeReport: false));
        var baseGold = Sample(baseReport).GetProperty("Gold").GetInt32();
        Assert.True(Sample(mastered).GetProperty("Gold").GetInt32() > baseGold);
        Assert.Equal(2, Sample(mastered).GetProperty("Mastery").GetInt32());
        Assert.Equal(2, mastered.RootElement.GetProperty("Assumptions").GetProperty("Mastery").GetInt32());
    }

    [Fact]
    public async Task ProductionThreePercentVarianceChangesDamageAndReproducesCompleteTrace()
    {
        var settings = JsonNode.Parse(File.ReadAllText(_config))!;
        settings["DungeonEncounters"]!["Dungeons"]!["simulator-isolated-boss"]![0]!["Monsters"]![0]!["MaxHp"] = 2000;
        File.WriteAllText(_config, settings.ToJsonString());
        using var fixedDamage = JsonDocument.Parse(await BalanceSimulatorApplication.RunAsync(Arguments(1), writeReport: false));
        settings["CombatDamage"]!["VariancePercent"] = 3;
        File.WriteAllText(_config, settings.ToJsonString());
        using var varied = JsonDocument.Parse(await BalanceSimulatorApplication.RunAsync(Arguments(1), writeReport: false));
        using var repeated = JsonDocument.Parse(await BalanceSimulatorApplication.RunAsync(Arguments(1), writeReport: false));
        var sample = Sample(varied);
        Assert.Equal(0, Sample(fixedDamage).GetProperty("DirectDamageVariancePercent").GetDecimal());
        Assert.Equal(3, sample.GetProperty("DirectDamageVariancePercent").GetDecimal());
        Assert.Equal(3, varied.RootElement.GetProperty("Assumptions").GetProperty("DirectDamageVariancePercent").GetDecimal());
        Assert.Contains("fractional part", varied.RootElement.GetProperty("Assumptions").GetProperty("DirectDamageRounding").GetString());
        Assert.Equal(sample.GetProperty("RoundTrace").GetRawText(), Sample(repeated).GetProperty("RoundTrace").GetRawText());
        Assert.Equal(sample.GetProperty("BattleFingerprint").GetString(), Sample(repeated).GetProperty("BattleFingerprint").GetString());
        Assert.Equal(sample.GetProperty("RuleRevision").GetString(), Sample(repeated).GetProperty("RuleRevision").GetString());
        Assert.NotEqual(sample.GetProperty("BattleFingerprint").GetString(), Sample(fixedDamage).GetProperty("BattleFingerprint").GetString());
        static int[] CharacterDamage(JsonElement scenario) => scenario.GetProperty("RoundTrace").EnumerateArray()
            .SelectMany(round => round.GetProperty("Data").GetProperty("Events").EnumerateArray())
            .Where(fact => fact.GetProperty("Kind").GetInt32() == (int)BattleEventKind.Damage &&
                fact.GetProperty("Source").GetString() == "character:1")
            .Select(fact => fact.GetProperty("CalculatedAmount").GetInt32()).ToArray();
        Assert.Contains(CharacterDamage(sample).Zip(CharacterDamage(Sample(fixedDamage))), pair => pair.First != pair.Second);
    }

    [Theory]
    [InlineData("Fire", "t1-candle-staff", "t1-burning-blade-hatchet")]
    [InlineData("Water", "t1-ice-tusk-mallet", "t1-fishbone-knife")]
    [InlineData("Earth", "t1-stone-edge-hatchet", "t1-boar-tusk-club")]
    [InlineData("Wind", "t1-feather-short-staff", "t1-hide-wrapped-club")]
    [InlineData("Light", "t1-sentry-old-sword", "t1-dusty-prayer-mace")]
    [InlineData("Dark", "t1-dim-apprentice-staff", "t1-grave-thorn-staff")]
    public async Task EntryReferenceRecordsLevelEightAndTheDeclaredRegionalEquipment(string element, string first, string second)
    {
        var arguments = Arguments(1);
        arguments[Array.IndexOf(arguments, "--stages") + 1] = "entry";
        using var report = JsonDocument.Parse(await BalanceSimulatorApplication.RunAsync(
            arguments.Concat(new[] { "--weapon-element", element }).ToArray(), writeReport: false));
        var build = Sample(report).GetProperty("Builds")[0];
        Assert.Equal(8, build.GetProperty("Level").GetInt32());
        var weapons = build.GetProperty("Weapons").EnumerateArray().ToArray();
        Assert.Equal(10, weapons.Length);
        Assert.Equal(first, weapons[0].GetProperty("Code").GetString());
        Assert.Equal(3, weapons[0].GetProperty("QualityRank").GetInt32());
        var firstSkills = weapons[0].GetProperty("Skills").EnumerateArray().ToArray();
        Assert.Equal(2, firstSkills.Length);
        Assert.All(firstSkills, skill => Assert.Equal(10, skill.GetProperty("Level").GetInt32()));
        Assert.Equal(second, weapons[1].GetProperty("Code").GetString());
        Assert.Equal(0, weapons[1].GetProperty("QualityRank").GetInt32());
        Assert.Equal(4, weapons[1].GetProperty("Skills")[0].GetProperty("Level").GetInt32());
        Assert.All(weapons.Skip(2), weapon =>
        {
            Assert.Equal($"t1-shop-{element.ToLowerInvariant()}", weapon.GetProperty("Code").GetString());
            Assert.Equal(1, weapon.GetProperty("Skills")[0].GetProperty("Level").GetInt32());
        });
    }

    [Fact]
    public async Task DefeatReportsDeathsCasualtiesAndActualPotionStock()
    {
        var settings = JsonNode.Parse(File.ReadAllText(_config))!;
        var monster = settings["DungeonEncounters"]!["Dungeons"]!["simulator-isolated-boss"]![0]!["Monsters"]![0]!;
        monster["MaxHp"] = 100_000;
        monster["Attack"] = 1_000_000;
        File.WriteAllText(_config, settings.ToJsonString());
        using var report = JsonDocument.Parse(await BalanceSimulatorApplication.RunAsync(Arguments(1), writeReport: false));
        var sample = Sample(report);
        Assert.False(sample.GetProperty("Victory").GetBoolean());
        Assert.Equal("PartyDefeated", sample.GetProperty("FailureCategory").GetString());
        Assert.Equal(1, sample.GetProperty("Casualties").GetInt32());
        Assert.Equal(1, sample.GetProperty("CharacterDeathEvents").GetInt32());
        Assert.Equal(0, sample.GetProperty("RemainingPotions").GetInt32());
        Assert.Equal(0, sample.GetProperty("PotionsUsed").GetInt32());
        Assert.All(sample.GetProperty("BossSkillUses").EnumerateObject(), skill => Assert.StartsWith("simulator-", skill.Name));
    }

    [Theory]
    [InlineData("Fire", "t1-candle-staff", "t1-burning-blade-hatchet", "t1-soot-iron-hammer")]
    [InlineData("Water", "t1-ice-tusk-mallet", "t1-fishbone-knife", "t1-frostmane-hunting-spear")]
    [InlineData("Earth", "t1-stone-edge-hatchet", "t1-boar-tusk-club", "t1-chipped-mining-pick")]
    [InlineData("Wind", "t1-feather-short-staff", "t1-hide-wrapped-club", "t1-sinew-shortbow")]
    [InlineData("Light", "t1-sentry-old-sword", "t1-dusty-prayer-mace", "t1-copper-ring-ritual-staff")]
    [InlineData("Dark", "t1-dim-apprentice-staff", "t1-grave-thorn-staff", "t1-wood-hilt-ritual-dagger")]
    public async Task DeepEntryEquipmentUsesAllThreeHuntsWithFullBreakthroughAndTenOrSevenSkills(
        string element, string first, string second, string third)
    {
        var arguments = Arguments(1);
        arguments[Array.IndexOf(arguments, "--stages") + 1] = "deep-entry";
        using var report = JsonDocument.Parse(await BalanceSimulatorApplication.RunAsync(
            arguments.Concat(new[] { "--weapon-element", element }).ToArray(), writeReport: false));
        var build = Sample(report).GetProperty("Builds")[0];
        Assert.Equal(10, build.GetProperty("Level").GetInt32());
        var weapons = build.GetProperty("Weapons").EnumerateArray().ToArray();
        Assert.Equal(10, weapons.Length);
        Assert.Equal(4, weapons.Count(weapon => weapon.GetProperty("Code").GetString() == first));
        Assert.Equal(3, weapons.Count(weapon => weapon.GetProperty("Code").GetString() == second));
        Assert.Equal(3, weapons.Count(weapon => weapon.GetProperty("Code").GetString() == third));
        Assert.All(weapons, weapon => Assert.Equal(3, weapon.GetProperty("QualityRank").GetInt32()));
        Assert.Equal(5, weapons.Count(weapon => weapon.GetProperty("Skills").EnumerateArray().All(skill => skill.GetProperty("Level").GetInt32() == 10)));
        Assert.Equal(5, weapons.Count(weapon => weapon.GetProperty("Skills").EnumerateArray().All(skill => skill.GetProperty("Level").GetInt32() == 7)));
        Assert.All(weapons, weapon => Assert.Equal(2, weapon.GetProperty("Skills").GetArrayLength()));
        var stats = build.GetProperty("Stats");
        Assert.Equal(weapons.Sum(weapon => weapon.GetProperty("Attack").GetInt32()), stats.GetProperty("BaseAttack").GetInt32());
        Assert.True(stats.GetProperty("MaxHp").GetInt32() > stats.GetProperty("BaseMaxHp").GetInt32());
    }

    [Fact]
    public async Task MemberElementsApplyToEachCharactersWholeLoadout()
    {
        var arguments = Arguments(1);
        arguments[Array.IndexOf(arguments, "--stages") + 1] = "deep-entry";
        using var report = JsonDocument.Parse(await BalanceSimulatorApplication.RunAsync(arguments.Concat(new[]
        {
            "--composition", "knight,priest,mage,hunter,rogue", "--weapon-elements", "Fire,Light,Water,Wind,Dark"
        }).ToArray(), writeReport: false));
        var builds = Sample(report).GetProperty("Builds").EnumerateArray().ToArray();
        var elements = new[] { "Fire", "Light", "Water", "Wind", "Dark" };
        Assert.Equal(elements.Length, builds.Length);
        for (var index = 0; index < elements.Length; index++)
        {
            Assert.Equal(elements[index], builds[index].GetProperty("WeaponElement").GetString());
            Assert.All(builds[index].GetProperty("Weapons").EnumerateArray(), weapon =>
                Assert.Equal(elements[index], weapon.GetProperty("Element").GetString()));
        }
    }

    [Theory]
    [InlineData("Fire", 1200, 2736)]
    [InlineData("Water", 1110, 3528)]
    [InlineData("Earth", 1120, 2816)]
    [InlineData("Wind", 1150, 2640)]
    [InlineData("Light", 1130, 2300)]
    [InlineData("Dark", 1150, 2530)]
    public async Task FourDeepBenchmarkIncludesThirdSkillsAndMaxedTransitionWeapons(string element, int attack, int maxHp)
    {
        var arguments = Arguments(1);
        arguments[Array.IndexOf(arguments, "--stages") + 1] = "deep-four";
        using var report = JsonDocument.Parse(await BalanceSimulatorApplication.RunAsync(
            arguments.Concat(new[] { "--weapon-element", element }).ToArray(), writeReport: false));
        var sample = Sample(report);
        var build = sample.GetProperty("Builds")[0];
        Assert.Equal(10, build.GetProperty("Level").GetInt32());
        var weapons = build.GetProperty("Weapons").EnumerateArray().ToArray();
        Assert.Equal(10, weapons.Length);
        var deep = weapons.Where(weapon => weapon.GetProperty("Code").GetString()!.StartsWith("t1-deep-")).ToArray();
        Assert.Equal(4, deep.Length);
        Assert.All(deep.GroupBy(weapon => weapon.GetProperty("Code").GetString()), group => Assert.Equal(2, group.Count()));
        Assert.All(deep, weapon => Assert.Equal(3, weapon.GetProperty("Skills").GetArrayLength()));
        Assert.All(weapons.Except(deep), weapon => Assert.Equal(2, weapon.GetProperty("Skills").GetArrayLength()));
        Assert.All(weapons, weapon =>
        {
            Assert.Equal(3, weapon.GetProperty("QualityRank").GetInt32());
            Assert.All(weapon.GetProperty("Skills").EnumerateArray(), skill => Assert.Equal(10, skill.GetProperty("Level").GetInt32()));
        });
        Assert.Equal(attack, build.GetProperty("Stats").GetProperty("BaseAttack").GetInt32());
        Assert.Equal(maxHp, build.GetProperty("Stats").GetProperty("MaxHp").GetInt32());
        Assert.Equal(0, sample.GetProperty("PotionsUsed").GetInt32());
    }

    [Fact]
    public async Task RejectsMemberElementCountAndConflictingElementOptions()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => BalanceSimulatorApplication.RunAsync(
            Arguments(1).Concat(new[] { "--weapon-elements", "Fire,Water" }).ToArray(), writeReport: false));
        await Assert.ThrowsAsync<ArgumentException>(() => BalanceSimulatorApplication.RunAsync(
            Arguments(1).Concat(new[] { "--weapon-elements", "Fire", "--weapon-element", "Fire" }).ToArray(), writeReport: false));
        await Assert.ThrowsAsync<ArgumentException>(() => BalanceSimulatorApplication.RunAsync(
            Arguments(1).Concat(new[] { "--weapon-elements", "99" }).ToArray(), writeReport: false));
    }

    [Theory]
    [InlineData("Earth", "knight", "t1-deep-stone-oath-maul")]
    [InlineData("Light", "priest", "t1-deep-dawn-ward-staff")]
    [InlineData("Water", "mage", "t1-deep-mirror-tide-blade")]
    [InlineData("Fire", "hunter", "t1-deep-embermark-axe")]
    [InlineData("Wind", "hunter", "t1-deep-swiftfeather-bow")]
    [InlineData("Dark", "rogue", "t1-deep-night-mark-twinblades")]
    public async Task OneDeepBenchmarkSelectsRoleTemplateAndKeepsFullBreakthroughLv7Skills(string element, string role, string deepCode)
    {
        var arguments = Arguments(1);
        arguments[Array.IndexOf(arguments, "--stages") + 1] = "deep-two";
        arguments[Array.IndexOf(arguments, "--roles") + 1] = role;
        using var report = JsonDocument.Parse(await BalanceSimulatorApplication.RunAsync(
            arguments.Concat(new[] { "--weapon-element", element }).ToArray(), writeReport: false));
        var build = Sample(report).GetProperty("Builds")[0];
        var weapons = build.GetProperty("Weapons").EnumerateArray().ToArray();
        Assert.Equal(10, weapons.Length);
        Assert.Equal(deepCode, weapons[0].GetProperty("Code").GetString());
        Assert.Equal(3, weapons[0].GetProperty("Skills").GetArrayLength());
        Assert.Single(weapons, w => w.GetProperty("Code").GetString()!.StartsWith("t1-deep-"));
        Assert.Equal(21, weapons.Sum(w => w.GetProperty("Skills").GetArrayLength()));
        Assert.All(weapons.Skip(1).GroupBy(w => w.GetProperty("Code").GetString()), group => Assert.Equal(3, group.Count()));
        for (var slot = 0; slot < 10; slot++)
        {
            Assert.Equal(3, weapons[slot].GetProperty("QualityRank").GetInt32());
            Assert.All(weapons[slot].GetProperty("Skills").EnumerateArray(), skill =>
                Assert.Equal(slot < 6 ? 10 : 7, skill.GetProperty("Level").GetInt32()));
        }
    }

    [Fact]
    public async Task ExplicitWorldIsHashedAndLegacyDefaultsAreRetained()
    {
        var defaults = SimulatorArguments.Parse([]);
        Assert.Null(defaults.DungeonCode);
        Assert.Equal(1, defaults.Depth);
        Assert.Equal(0, defaults.Mastery);
        Assert.Equal(1000, defaults.StartingPotions);
        Assert.Equal(1, defaults.Players);
        using var report = JsonDocument.Parse(await BalanceSimulatorApplication.RunAsync(Arguments(1), writeReport: false));
        Assert.True(report.RootElement.GetProperty("SourceFiles").TryGetProperty(_world.Replace('\\', '/'), out _));
        Assert.Equal(_world, report.RootElement.GetProperty("Assumptions").GetProperty("WorldFile").GetString());
    }

    [Theory]
    [InlineData("Fire")]
    [InlineData("Water")]
    [InlineData("Earth")]
    [InlineData("Wind")]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task TwoDeepBenchmarkIncludesBothTemplatesAndEightMaxedHuntWeapons(string element)
    {
        var arguments = Arguments(1);
        arguments[Array.IndexOf(arguments, "--stages") + 1] = "deep-three";
        using var report = JsonDocument.Parse(await BalanceSimulatorApplication.RunAsync(
            arguments.Concat(new[] { "--weapon-element", element }).ToArray(), writeReport: false));
        var build = Sample(report).GetProperty("Builds")[0];
        Assert.Equal(10, build.GetProperty("Level").GetInt32());
        var weapons = build.GetProperty("Weapons").EnumerateArray().ToArray();
        Assert.Equal(10, weapons.Length);
        var deep = weapons.Where(w => w.GetProperty("Code").GetString()!.StartsWith("t1-deep-")).ToArray();
        Assert.Equal(2, deep.Length);
        Assert.Equal(2, deep.Select(w => w.GetProperty("Code").GetString()).Distinct().Count());
        Assert.All(deep, w => Assert.Equal(3, w.GetProperty("Skills").GetArrayLength()));
        Assert.All(weapons.Except(deep), w => Assert.Equal(2, w.GetProperty("Skills").GetArrayLength()));
        Assert.Equal(new[] { 2, 3, 3 }, weapons.Except(deep).GroupBy(w => w.GetProperty("Code").GetString())
            .Select(group => group.Count()).Order().ToArray());
        Assert.Equal(22, weapons.Sum(w => w.GetProperty("Skills").GetArrayLength()));
        Assert.All(weapons, weapon =>
        {
            Assert.Equal(3, weapon.GetProperty("QualityRank").GetInt32());
            Assert.All(weapon.GetProperty("Skills").EnumerateArray(), skill => Assert.Equal(10, skill.GetProperty("Level").GetInt32()));
        });
    }

    [Theory]
    [InlineData("--depth", "0")]
    [InlineData("--depth", "101")]
    [InlineData("--depth", "1.5")]
    [InlineData("--mastery", "5")]
    [InlineData("--mastery", "-1")]
    [InlineData("--starting-potions", "-1")]
    [InlineData("--starting-potions", "2147483648")]
    [InlineData("--players", "0")]
    [InlineData("--players", "6")]
    [InlineData("--players", "1.5")]
    public void RejectsInvalidScenarioNumbers(string option, string value) =>
        Assert.Throws<ArgumentException>(() => SimulatorArguments.Parse([option, value]));

    [Fact]
    public void RejectsMissingDuplicateUnknownAndConflictingOptions()
    {
        Assert.Throws<ArgumentException>(() => SimulatorArguments.Parse(["--depth"]));
        Assert.Throws<ArgumentException>(() => SimulatorArguments.Parse(["--depth", "--trace"]));
        Assert.Throws<ArgumentException>(() => SimulatorArguments.Parse(["--depth", "1", "--depth", "4"]));
        Assert.Throws<ArgumentException>(() => SimulatorArguments.Parse(["--mystery", "1"]));
        Assert.Throws<ArgumentException>(() => SimulatorArguments.Parse(["--dungeon-code", "boss", "--targets", "endgame"]));
    }

    [Fact]
    public async Task RejectsUnknownDungeonAndDepthBeyondThatDungeonsConfiguredRange()
    {
        var unknown = Arguments(1);
        unknown[Array.IndexOf(unknown, "--dungeon-code") + 1] = "not-defined";
        await Assert.ThrowsAsync<ArgumentException>(() => BalanceSimulatorApplication.RunAsync(unknown, writeReport: false));
        var normal = Arguments(4);
        normal[Array.IndexOf(normal, "--dungeon-code") + 1] = "kobold-mine";
        await Assert.ThrowsAsync<ArgumentException>(() => BalanceSimulatorApplication.RunAsync(normal, writeReport: false));
    }

    [Fact]
    public async Task RejectsMorePlayersThanCharactersInTheScenario() =>
        await Assert.ThrowsAsync<ArgumentException>(() => BalanceSimulatorApplication.RunAsync(
            Arguments(1).Concat(new[] { "--players", "2" }).ToArray(), writeReport: false));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task MultipleAccountsKeepAutoCombatAndCharacterOwnedRewards(int players)
    {
        var settings = JsonNode.Parse(File.ReadAllText(_config))!;
        settings["Rewards"]!["CoopDropBonus"] = new JsonObject { ["PercentPerAdditionalUser"] = 10, ["MaximumPercent"] = 40 };
        settings["DungeonEncounters"]!["Dungeons"]!["simulator-isolated-boss"]![0]!["Monsters"]![0]!["MaxHp"] = 2000;
        foreach (var (bundle, quantity) in new[] { ("MonsterKills", 2), ("DungeonClears", 3) })
            settings["Rewards"]![bundle]!["simulator-isolated-boss"]!["Drops"] = new JsonArray(new JsonObject
            {
                ["Kind"] = "Consumable", ["Code"] = "minor-healing-potion", ["Quantity"] = quantity, ["ChancePercent"] = 100
            });
        File.WriteAllText(_config, settings.ToJsonString());
        using var report = JsonDocument.Parse(await BalanceSimulatorApplication.RunAsync(Arguments(1).Concat(new[]
            { "--players", players.ToString(), "--composition", "knight,knight,knight,knight,knight" }).ToArray(), writeReport: false));
        var sample = Sample(report);
        Assert.True(sample.GetProperty("Victory").GetBoolean());
        Assert.Equal(players, sample.GetProperty("Players").GetInt32());
        Assert.Equal(players, report.RootElement.GetProperty("Assumptions").GetProperty("Players").GetInt32());
        var regularEvents = sample.GetProperty("CoopRewardEvents").EnumerateArray().Where(entry =>
            entry.GetProperty("EventKey").GetString() == "clear" || entry.GetProperty("EventKey").GetString()!.StartsWith("monster:")).ToArray();
        Assert.Equal(2, regularEvents.Length);
        Assert.All(regularEvents, entry =>
        {
            Assert.Equal(players, entry.GetProperty("CoopParticipantCount").GetInt32());
            Assert.Equal((players - 1) * 10, entry.GetProperty("CoopDropBonusPercent").GetDecimal());
        });
        var rewards = sample.GetProperty("Rewards");
        var characters = rewards.GetProperty("Characters").EnumerateArray().ToArray();
        Assert.Equal(5, characters.Length);
        for (var index = 0; index < characters.Length; index++)
        {
            Assert.Equal(index + 1, characters[index].GetProperty("CharacterId").GetInt32());
            Assert.Equal(index % players + 1, characters[index].GetProperty("UserId").GetInt32());
            Assert.Equal(1000, characters[index].GetProperty("Earned").GetProperty("Gold").GetInt32());
            Assert.Equal(5, characters[index].GetProperty("Earned").GetProperty("OrdinaryItemQuantity").GetInt32());
        }
        var accounts = rewards.GetProperty("Accounts").EnumerateArray().ToArray();
        Assert.Equal(players, accounts.Length);
        Assert.Equal(5000, sample.GetProperty("Gold").GetInt32());
        Assert.Equal(25, rewards.GetProperty("Team").GetProperty("OrdinaryItemQuantity").GetInt32());
        foreach (var account in accounts)
        {
            var ownedCount = characters.Count(character => character.GetProperty("UserId").GetInt32() == account.GetProperty("UserId").GetInt32());
            Assert.Equal(ownedCount, account.GetProperty("CharacterIds").GetArrayLength());
            Assert.Equal(ownedCount * 1000, account.GetProperty("Earned").GetProperty("Gold").GetInt32());
            Assert.Equal(ownedCount * 5, account.GetProperty("Earned").GetProperty("OrdinaryItemQuantity").GetInt32());
        }
    }

    [Fact]
    public async Task DefaultPlayersAndExplicitSingleAccountHaveTheSameBattleAndEarnings()
    {
        using var implicitReport = JsonDocument.Parse(await BalanceSimulatorApplication.RunAsync(Arguments(1), writeReport: false));
        using var explicitReport = JsonDocument.Parse(await BalanceSimulatorApplication.RunAsync(
            Arguments(1).Concat(new[] { "--players", "1" }).ToArray(), writeReport: false));
        Assert.Equal(Sample(implicitReport).GetProperty("BattleFingerprint").GetString(), Sample(explicitReport).GetProperty("BattleFingerprint").GetString());
        Assert.Equal(Sample(implicitReport).GetProperty("RoundTrace").GetRawText(), Sample(explicitReport).GetProperty("RoundTrace").GetRawText());
        Assert.Equal(Sample(implicitReport).GetProperty("Rewards").GetRawText(), Sample(explicitReport).GetProperty("Rewards").GetRawText());
    }

    [Fact]
    public void OrdinaryItemSummaryExcludesOtherGrantSourcesAndPendingLedger()
    {
        var actors = new[] { new Character { Id = 1, UserId = 1, Gold = 11 }, new Character { Id = 2, UserId = 1, Gold = 7 } };
        RewardEntry Entry(string source = "Base", string key = "clear", string kind = "Material", int quantity = 3) => new()
        {
            UserId = 1, CharacterId = 1, RewardSource = source, EventKey = key, Kind = kind, Code = "item", Quantity = quantity
        };
        var ledger = new[] { Entry(), Entry(key: "monster:1:1", quantity: 2), Entry("Mastery"), Entry("Challenge"),
            Entry(key: "rare-seed"), Entry(key: "first-clear"), Entry(key: "starter-hunt-weapon", kind: "Weapon"), Entry(kind: "Gold") };
        var settled = SimulatorRewardSummary.Build(actors, ledger, "Victory");
        Assert.Equal(5, settled.Team.OrdinaryItemQuantity);
        Assert.Equal(18, settled.Team.Gold);
        Assert.Equal(5, settled.Characters[0].Earned.OrdinaryItemQuantity);
        Assert.Equal(0, settled.Characters[1].Earned.OrdinaryItemQuantity);
        Assert.Equal(18, settled.Accounts[0].Earned.Gold);
        Assert.Equal(0, SimulatorRewardSummary.Build(actors, ledger, "Pending").Team.OrdinaryItemQuantity);
    }

    [Fact]
    public void MetricsCountStableCodesOncePerActorRoundAndUseTerminalFacts()
    {
        var metrics = new SimulatorMetrics([9, 10]);
        BattleEventResponse Fact(int sequence, int source = 9, string name = "Display A") => new()
        {
            Sequence = sequence, RunSequence = 1, RoundNumber = 2, Kind = BattleEventKind.Damage,
            ActionKind = BattleActionKind.Skill, SkillCode = "stable-boss-skill", Source = new("Monster", source, Name: name),
            Target = new("Character", sequence), HpBefore = 10, HpAfter = 0
        };
        metrics.Observe([Fact(1), Fact(2, name: "改名后的显示文字"), Fact(3, source: 10), Fact(1)]);
        Assert.Equal(2, metrics.BossSkillUses["stable-boss-skill"]);
        Assert.Equal(3, metrics.CharacterDeaths);
        Assert.Equal("PartyDefeated", SimulatorMetrics.FailureCategory(false, RoomStatus.BattleOver, 0, 3, 250));
        Assert.Equal("RoundLimit", SimulatorMetrics.FailureCategory(false, RoomStatus.Cooldown, 1, 250, 250));
        Assert.Equal("None", SimulatorMetrics.FailureCategory(true, RoomStatus.BattleOver, 1, 3, 250));
    }

    [Fact]
    public void ExpiringOrRemovingBossArmorDoesNotCountAsAnotherCast()
    {
        var metrics = new SimulatorMetrics([9]);
        BattleEventResponse Armor(int round, BattleStatusChange change) => new()
        {
            RunSequence = 1, RoundNumber = round, Kind = BattleEventKind.Status,
            ActionKind = BattleActionKind.Skill, SkillCode = "boss-armor", Source = new("Monster", 9),
            Target = new("Monster", 9), StatusChange = change
        };
        metrics.Observe([Armor(3, BattleStatusChange.Added), Armor(5, BattleStatusChange.Expired),
            Armor(6, BattleStatusChange.Removed), Armor(7, BattleStatusChange.Consumed),
            Armor(11, BattleStatusChange.Refreshed)]);
        Assert.Equal(2, metrics.BossSkillUses["boss-armor"]);
    }

    private string[] Arguments(int depth) => ["--config", _config, "--world", _world, "--dungeon-code", "simulator-isolated-boss",
        "--depth", depth.ToString(), "--starting-potions", "0", "--stages", "starter", "--roles", "knight",
        "--runs", "1", "--seed-start", "7213", "--trace"];
    private static JsonElement Sample(JsonDocument report) => report.RootElement.GetProperty("Samples")[0];
    public void Dispose()
    {
        File.Delete(_config);
        File.Delete(_world);
        Directory.Delete(_fixtureDirectory);
        Directory.SetCurrentDirectory(_previousDirectory);
    }
}
