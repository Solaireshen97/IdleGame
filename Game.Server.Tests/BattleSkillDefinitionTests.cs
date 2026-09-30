using System.Text.Json;
using System.Text.Json.Nodes;
using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class BattleSkillDefinitionTests
{
    private static string RepositoryFile(string path) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", path));

    [Fact]
    public void EveryProductionCharacterVersionMatchesFrozenBehaviorAndUnlockBoundaries()
    {
        var config = new ConfigurationBuilder().AddJsonFile(RepositoryFile("Game.Server/appsettings.json")).Build();
        var options = config.GetSection(SkillOptions.SectionName).Get<SkillOptions>()!;
        var monsters = new MonsterCombatCatalog(Options.Create(config.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!));
        var catalog = new SkillCatalog(Options.Create(options), monsters);
        var baseline = JsonNode.Parse(File.ReadAllText(RepositoryFile("Game.Server.Tests/Fixtures/SkillBehaviorBaseline.json")))!;
        Assert.Equal(80, baseline["Characters"]!.AsArray().Count);
        foreach (var expected in baseline["Characters"]!.AsArray())
        {
            var code = expected!["Code"]!.GetValue<string>();
            var rank = expected["Level"]!.GetValue<int>();
            var shared = expected["IsShared"]!.GetValue<bool>();
            var source = options.Abilities.Single(skill => skill.Code == code);
            var level = rank == 3 ? source.Level3UnlockLevel : rank == 2 ? source.Level2UnlockLevel : source.UnlockLevel;
            var character = new Character { ProfessionCode = shared ? "other" : source.ProfessionCode, Level = shared ? 10 : level };
            var skill = catalog.Resolve(character, code, new Dictionary<string, int> { [source.ProfessionCode] = 30 });
            Assert.NotNull(skill);
            Assert.True(JsonNode.DeepEquals(expected, JsonSerializer.SerializeToNode(new
            {
                skill.Code, skill.ProfessionCode, skill.Level, skill.IsShared, skill.CooldownRounds, skill.InitialCooldownRounds,
                skill.UnlockLevel, skill.Level2UnlockLevel, skill.Level3UnlockLevel, skill.AutoCondition,
                skill.ConditionalDamageBonusPercent, skill.RequiredTargetStatusCode, skill.TargetHpBelowPercent,
                Effects = skill.Effects.Select(DescribeEffect)
            })), $"Changed behavior for {code}, rank {rank}, shared {shared}");
            if (!shared)
            {
                character.Level = level - 1;
                var previous = catalog.Resolve(character, code);
                if (rank == 1) Assert.Null(previous);
                else Assert.Equal(rank - 1, previous!.Level);
            }
        }
    }

    [Fact]
    public void EveryProductionMonsterSkillMatchesFrozenEffectsAndIntentRules()
    {
        var config = new ConfigurationBuilder().AddJsonFile(RepositoryFile("Game.Server/appsettings.json")).Build();
        var catalog = new MonsterCombatCatalog(Options.Create(config.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!));
        var baseline = JsonNode.Parse(File.ReadAllText(RepositoryFile("Game.Server.Tests/Fixtures/SkillBehaviorBaseline.json")))!;
        Assert.Equal(60, baseline["Monsters"]!.AsArray().Count);
        foreach (var expected in baseline["Monsters"]!.AsArray())
        {
            var skill = catalog.ResolveSkill(expected!["Code"]!.GetValue<string>())!;
            Assert.True(JsonNode.DeepEquals(expected, JsonSerializer.SerializeToNode(new
            {
                skill.Code, skill.CooldownRounds, skill.TargetType, skill.SelfHpBelowPercent, skill.RoomRoundAtLeast,
                skill.ForcedPriority, skill.IsInterruptible, skill.DangerLevel, Effects = skill.Effects.Select(DescribeEffect)
            })), $"Changed monster behavior for {skill.Code}");
            Assert.All(skill.Effects, effect => Assert.Equal(skill.TargetType == "Self" ? BattleTargetSide.Self : BattleTargetSide.Opponent,
                effect.TargetPolicy.Side));
        }
    }

    [Fact]
    public void ResolvedVersionsAreIsolatedFromConfigAndLegacyCopiesAndDoNotAccumulateRankTwoOverrides()
    {
        var options = CreateOptions();
        options.Abilities[0].Level2 = new() { CooldownRounds = 8, Effects = [new() { Type = "Damage", Target = "Monster", Power = 20 }] };
        options.Abilities[0].Level3 = new() { Name = "Rank three" };
        options.Abilities[0].SharedVersion = new() { Description = "Shared" };
        var catalog = new SkillCatalog(Options.Create(options));
        var character = new Character { ProfessionCode = "knight", Level = 30 };
        var rankThree = catalog.Resolve(character, "strike")!;
        Assert.Equal(2, rankThree.CooldownRounds);
        Assert.Equal(10, rankThree.Effects[0].Power);
        options.Abilities[0].Effects[0].Power = 999;
        options.Professions[0].SharedSkillCode = "missing";
        catalog.FindSkill("strike")!.Effects[0].Power = 888;
        catalog.FindProfession("knight")!.SharedSkillCode = "missing";
        Assert.Same(rankThree, catalog.Resolve(character, "strike"));
        Assert.Equal(10, rankThree.Effects[0].Power);
        character.ProfessionCode = "other";
        character.Level = 10;
        var shared = catalog.Resolve(character, "strike", new Dictionary<string, int> { ["knight"] = 30 })!;
        Assert.True(shared.IsShared);
        Assert.Equal("Rank three", shared.Name);
        Assert.Equal("Shared", shared.Description);
        Assert.Equal(10, shared.Effects[0].Power);
        Assert.Null(catalog.Resolve(character, "strike"));
        Assert.Null(catalog.ResolveSkillForLevel(character, "strike"));
        Assert.Null(catalog.Resolve(character, "strike", new Dictionary<string, int>()));
    }

    [Theory]
    [InlineData("Level2", "UnknownStatus")]
    [InlineData("Level3", "UnknownCondition")]
    [InlineData("Shared", "NoDamage")]
    [InlineData("Shared", "EmptyEffects")]
    public void InvalidResolvedVariantsFailAtStartup(string version, string fault)
    {
        var options = CreateOptions();
        var variant = fault switch
        {
            "UnknownStatus" => new CombatSkillVariantOptions { Effects = [new() { Type = "ApplyStatus", Target = "Self", StatusCode = "missing", DurationRounds = 1 }] },
            "UnknownCondition" => new CombatSkillVariantOptions { ConditionalDamageBonusPercent = 10, RequiredTargetStatusCode = "missing" },
            "NoDamage" => new CombatSkillVariantOptions { ConditionalDamageBonusPercent = 10, TargetHpBelowPercent = 50,
                Effects = [new() { Type = "Guard", Target = "Self", Power = 10 }] },
            _ => new CombatSkillVariantOptions { Effects = [] }
        };
        if (version == "Level2") options.Abilities[0].Level2 = variant;
        else if (version == "Level3") options.Abilities[0].Level3 = variant;
        else options.Abilities[0].SharedVersion = variant;
        var monsters = new MonsterCombatCatalog(Options.Create(new MonsterCombatOptions()));
        Assert.Throws<InvalidOperationException>(() => new SkillCatalog(Options.Create(options), monsters));
    }

    [Fact]
    public void MonsterRuntimeProfileAndEffectsAreIndependentFromMutableInput()
    {
        var options = new MonsterCombatOptions
        {
            Skills = [new() { Code = "slam", Name = "Slam", Description = "Damage", DamagePowerPercent = 150 }],
            Profiles = new() { ["boss"] = new() { SkillUseChancePercent = 100, Skills = [new() { Code = "slam" }] } }
        };
        var catalog = new MonsterCombatCatalog(Options.Create(options));
        options.Skills[0].DamagePowerPercent = 999;
        options.Profiles["boss"].Skills.Clear();
        catalog.FindSkill("slam")!.DamagePowerPercent = 888;
        Assert.Equal(150, catalog.ResolveSkill("slam")!.Effects[0].AttackPowerPercent);
        Assert.Single(catalog.ResolveProfile("boss")!.Skills);
        Assert.Equal(4, catalog.ResolveProfile(catalog.ResolveDepthProfile("boss", 4))!.Skills.Length);
    }

    private static object DescribeEffect(BattleSkillEffect effect) => new
    {
        effect.Type, effect.Target, effect.Power, effect.AttackPowerPercent, effect.HealMaxHpPercent,
        effect.StatusCode, effect.DurationRounds
    };

    private static SkillOptions CreateOptions() => new()
    {
        Professions = [new() { Code = "knight", Name = "Knight", StartingSkills = ["strike"], SharedSkillCode = "strike" }],
        Abilities = [new() { Code = "strike", ProfessionCode = "knight", Name = "Strike", Description = "Damage", CooldownRounds = 2,
            UnlockLevel = 1, Level2UnlockLevel = 12, Level3UnlockLevel = 22,
            Effects = [new() { Type = "Damage", Target = "Monster", Power = 10 }] }]
    };
}
