using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Models;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class SkillProgressionFrameworkTests
{
    private static SkillCatalog CreateCatalog() => new(Options.Create(new SkillOptions
    {
        Professions =
        [
            new() { Code = "swordsman", Name = "剑士", StartingSkills = ["sword"], SharedSkillCode = "sword" },
            new() { Code = "mage", Name = "法师", StartingSkills = ["spell"], SharedSkillCode = "spell" }
        ],
        Abilities =
        [
            new() { Code = "sword", ProfessionCode = "swordsman", Name = "斩击", Description = "伤害",
                EffectType = "Damage", Power = 10, CooldownRounds = 2, InitialCooldownRounds = 1,
                UnlockLevel = 5, Level2UnlockLevel = 12, Level3UnlockLevel = 25,
                Level2 = new() { Power = 20 }, Level3 = new() { Power = 30 },
                SharedVersion = new() { Power = 7, InitialCooldownRounds = 3 } },
            new() { Code = "spell", ProfessionCode = "mage", Name = "法术", Description = "伤害",
                EffectType = "Damage", Power = 10, CooldownRounds = 2, UnlockLevel = 1 }
        ]
    }));

    [Fact]
    public void OwnSkillUnlocksAndUpgradesFromConfiguredLevels()
    {
        var catalog = CreateCatalog();
        var character = new Character { ProfessionCode = "swordsman", Level = 4 };
        Assert.Null(catalog.ResolveSkillForLevel(character, "sword"));

        character.Level = 5;
        Assert.Equal(10, catalog.ResolveSkillForLevel(character, "sword")!.Power);
        character.Level = 12;
        Assert.Equal(20, catalog.ResolveSkillForLevel(character, "sword")!.Power);
        character.Level = 25;
        Assert.Equal(30, catalog.ResolveSkillForLevel(character, "sword")!.Power);
    }

    [Fact]
    public void SharedVersionNeedsSourceLevelThirtyAndCurrentLevelTen()
    {
        var catalog = CreateCatalog();
        var character = new Character { ProfessionCode = "mage", Level = 9 };
        var levels = new Dictionary<string, int> { ["swordsman"] = 30 };
        Assert.Null(catalog.ResolveSkillForLevel(character, "sword", levels));

        character.Level = 10;
        levels["swordsman"] = 29;
        Assert.Null(catalog.ResolveSkillForLevel(character, "sword", levels));
        levels["swordsman"] = 30;
        var shared = catalog.ResolveSkillForLevel(character, "sword", levels)!;
        Assert.Equal(7, shared.Power);
        Assert.Equal(3, shared.InitialCooldownRounds);
        Assert.Equal(30, catalog.FindSkill("sword")!.Level3!.Power);
    }
}
