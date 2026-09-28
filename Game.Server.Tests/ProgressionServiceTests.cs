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
        Assert.Equal([60, 120, 220, 350, 500, 700, 1000, 1400, 1900], settings.ExperienceToNextLevel.Take(9));
        Assert.Equal(30900, settings.ExperienceToNextLevel[^1]);
        Assert.Equal(282250, settings.ExperienceToNextLevel.Sum());
        Assert.Equal([100, 100, 100, 100, 100, 75, 40, 15, 0], settings.ExperiencePercentByLevelDifference);
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

        progression.AwardExperience(character, 300_000);
        var afterCap = progression.AwardExperience(character, 10);

        Assert.Equal(30, character.Level);
        Assert.Equal(0, character.TalentPoints);
        Assert.Equal(0, character.Experience);
        Assert.Null(progression.GetExperienceToNextLevel(character.Level));
        Assert.Equal(0, afterCap.ExperienceGained);
        Assert.Equal(0, afterCap.LevelsGained);
    }

    [Theory]
    [InlineData(5, 5, 100)]
    [InlineData(6, 5, 100)]
    [InlineData(7, 5, 100)]
    [InlineData(8, 5, 100)]
    [InlineData(9, 5, 100)]
    [InlineData(10, 5, 0)]
    [InlineData(6, 1, 75)]
    [InlineData(7, 1, 40)]
    [InlineData(8, 1, 15)]
    [InlineData(9, 1, 0)]
    public void ApplyDungeonExperienceModifier_UsesConfiguredLevelDifference(int characterLevel, int dungeonLevel, int expected)
    {
        var progression = ProgressionTestFactory.Create();

        var adjusted = progression.ApplyDungeonExperienceModifier(100, characterLevel, dungeonLevel);

        Assert.Equal(expected, adjusted);
    }

    private static ProgressionOptions ProductionSettings()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Game.Server", "appsettings.json"));
        return new ConfigurationBuilder().AddJsonFile(path).Build()
            .GetSection(ProgressionOptions.SectionName).Get<ProgressionOptions>()!;
    }

}
