using System.Text.Json;
using System.Text.Json.Nodes;
using Game.BalanceSimulator;
using Game.Shared.Dtos;
using Game.Shared.Enums;
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
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Game.Server", "appsettings.json"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Repository root not found.");
        Directory.SetCurrentDirectory(root.FullName);
        _fixtureDirectory = Path.Combine(Path.GetTempPath(), "idle-balance-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_fixtureDirectory);
        _config = Path.Combine(_fixtureDirectory, "settings.json");
        _world = Path.Combine(_fixtureDirectory, "world.json");
        var settings = JsonNode.Parse(File.ReadAllText("Game.Server/appsettings.json"))!;
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

    [Fact]
    public async Task ExplicitWorldIsHashedAndLegacyDefaultsAreRetained()
    {
        var defaults = SimulatorArguments.Parse([]);
        Assert.Null(defaults.DungeonCode);
        Assert.Equal(1, defaults.Depth);
        Assert.Equal(0, defaults.Mastery);
        Assert.Equal(1000, defaults.StartingPotions);
        using var report = JsonDocument.Parse(await BalanceSimulatorApplication.RunAsync(Arguments(1), writeReport: false));
        Assert.True(report.RootElement.GetProperty("SourceFiles").TryGetProperty(_world.Replace('\\', '/'), out _));
        Assert.Equal(_world, report.RootElement.GetProperty("Assumptions").GetProperty("WorldFile").GetString());
    }

    [Theory]
    [InlineData("--depth", "0")]
    [InlineData("--depth", "101")]
    [InlineData("--depth", "1.5")]
    [InlineData("--mastery", "5")]
    [InlineData("--mastery", "-1")]
    [InlineData("--starting-potions", "-1")]
    [InlineData("--starting-potions", "2147483648")]
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
