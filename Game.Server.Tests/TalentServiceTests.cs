using Game.Shared.Models;
using Xunit;

namespace Game.Server.Tests;

public sealed class TalentServiceTests
{
    [Fact]
    public void LevelOneStartsWithoutPointsAndLevelTenHasEarnedNine()
    {
        var progression = ProgressionTestFactory.Create();
        var character = new Character { Level = 1, TalentPoints = 0 };
        while (character.Level < 10)
            progression.AwardExperience(character, progression.GetExperienceToNextLevel(character.Level)!.Value);
        Assert.Equal((10, 9, 0), (character.Level, character.TalentPoints, character.Experience));
    }
}
