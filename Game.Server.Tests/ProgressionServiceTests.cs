using Game.Server.Configuration;
using Game.Shared.Models;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Game.Server.Tests;

public class ProgressionServiceTests
{
    [Fact]
    public void ProductionProgression_UsesSlowerTenLevelCurve()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Game.Server", "appsettings.json"));
        var settings = new ConfigurationBuilder().AddJsonFile(path).Build()
            .GetSection(ProgressionOptions.SectionName).Get<ProgressionOptions>()!;

        Assert.Equal([60, 120, 220, 350, 500, 700, 1000, 1400, 1900], settings.ExperienceToNextLevel);
        Assert.Equal(6250, settings.ExperienceToNextLevel.Sum());
        Assert.Equal([100, 75, 40, 15, 0], settings.ExperiencePercentByLevelDifference);
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
        Assert.Equal(2, character.TalentPoints);
        Assert.Equal((76, 100, 20), (character.Hp, character.MaxHp, character.Attack));
    }

    [Fact]
    public void AwardExperience_StopsAtConfiguredLevelCap()
    {
        var progression = ProgressionTestFactory.Create();
        var character = new Character { Level = 1 };

        progression.AwardExperience(character, 1_000);
        var afterCap = progression.AwardExperience(character, 10);

        Assert.Equal(10, character.Level);
        Assert.Equal(9, character.TalentPoints);
        Assert.Equal(0, character.Experience);
        Assert.Null(progression.GetExperienceToNextLevel(character.Level));
        Assert.Equal(0, afterCap.ExperienceGained);
        Assert.Equal(0, afterCap.LevelsGained);
    }

    [Theory]
    [InlineData(5, 5, 100)]
    [InlineData(6, 5, 75)]
    [InlineData(7, 5, 40)]
    [InlineData(8, 5, 15)]
    [InlineData(9, 5, 0)]
    [InlineData(10, 5, 0)]
    public void ApplyDungeonExperienceModifier_UsesConfiguredLevelDifference(int characterLevel, int dungeonLevel, int expected)
    {
        var progression = ProgressionTestFactory.Create();

        var adjusted = progression.ApplyDungeonExperienceModifier(100, characterLevel, dungeonLevel);

        Assert.Equal(expected, adjusted);
    }

}
