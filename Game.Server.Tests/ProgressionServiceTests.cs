using Game.Shared.Models;
using Xunit;

namespace Game.Server.Tests;

public class ProgressionServiceTests
{
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

}
