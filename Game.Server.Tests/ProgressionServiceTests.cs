using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public class ProgressionServiceTests
{
    [Fact]
    public void ProductionProgression_DefinesThirtyLevelCurve()
    {
        var settings = ProductionSettings();

        Assert.Equal(30, settings.MaximumLevel);
        Assert.Equal(29, settings.ExperienceToNextLevel.Count);
        Assert.Equal(Enumerable.Range(0, 29).Select(index => 50 + index * 25),
            settings.ExperienceToNextLevel);
        Assert.Equal(11_600, settings.ExperienceToNextLevel.Sum());
    }

    [Fact]
    public void AwardExperience_CanGrantMultipleLevelsWithoutChangingCombatStats()
    {
        var progression = ProgressionTestFactory.Create();
        var character = new Character { Level = 1, Hp = 76, MaxHp = 100, Attack = 20};

        var gain = progression.AwardExperience(character, 55);

        Assert.Equal(55, gain.ExperienceGained);
        Assert.Equal(2, gain.LevelsGained);
        Assert.Equal(3, character.Level);
        Assert.Equal(5, character.Experience);
        Assert.Equal(0, character.TalentPoints);
        Assert.Equal((76, 100, 20), (character.Hp, character.MaxHp, character.Attack));
    }

    [Fact]
    public void AwardExperience_StopsAtConfiguredLevelCap()
    {
        var progression = new ProgressionService(Options.Create(ProductionSettings()));
        var character = new Character { Level = 1 };

        var toCap = progression.AwardExperience(character, 11_600);
        var afterCap = progression.AwardExperience(character, 10);

        Assert.Equal(30, character.Level);
        Assert.Equal(29, toCap.LevelsGained);
        Assert.Equal(0, character.TalentPoints);
        Assert.Equal(0, character.Experience);
        Assert.Null(progression.GetExperienceToNextLevel(character.Level));
        Assert.Equal(0, afterCap.ExperienceGained);
        Assert.Equal(0, afterCap.LevelsGained);
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(10, 100)]
    [InlineData(29, 100)]
    [InlineData(30, 0)]
    public void DungeonExperienceOnlyStopsAtTheLevelCap(int characterLevel, int expected)
    {
        var progression = new ProgressionService(Options.Create(ProductionSettings()));

        var adjusted = progression.GetAwardableExperience(100, characterLevel);

        Assert.Equal(expected, adjusted);
    }

    private static ProgressionOptions ProductionSettings()
    {
        var path = TestRepository.File("Game.Server", "appsettings.json");
        return new ConfigurationBuilder().AddJsonFile(path).Build()
            .GetSection(ProgressionOptions.SectionName).Get<ProgressionOptions>()!;
    }

}
