using Game.Shared.Models;
using Xunit;

namespace Game.Server.Tests;

public class ProgressionServiceTests
{
    [Fact]
    public void AwardVictoryExperience_CanGrantMultipleLevelsWithoutChangingCombatStats()
    {
        var progression = ProgressionTestFactory.Create();
        var character = new Character { Level = 1, Hp = 76, MaxHp = 100, Attack = 20, Defense = 5 };

        var gain = progression.AwardVictoryExperience(character, 55);

        Assert.Equal(55, gain.ExperienceGained);
        Assert.Equal(2, gain.LevelsGained);
        Assert.Equal(3, character.Level);
        Assert.Equal(5, character.Experience);
        Assert.Equal(2, character.TalentPoints);
        Assert.Equal((76, 100, 20, 5), (character.Hp, character.MaxHp, character.Attack, character.Defense));
    }

    [Fact]
    public void AwardVictoryExperience_StopsAtConfiguredLevelCap()
    {
        var progression = ProgressionTestFactory.Create();
        var character = new Character { Level = 1 };

        progression.AwardVictoryExperience(character, 1_000);
        var afterCap = progression.AwardVictoryExperience(character, 10);

        Assert.Equal(10, character.Level);
        Assert.Equal(9, character.TalentPoints);
        Assert.Equal(0, character.Experience);
        Assert.Null(progression.GetExperienceToNextLevel(character.Level));
        Assert.Equal(0, afterCap.ExperienceGained);
        Assert.Equal(0, afterCap.LevelsGained);
    }

    [Fact]
    public void GetVictoryExperience_UsesDungeonRewardConfiguration()
    {
        var progression = ProgressionTestFactory.Create();

        Assert.Equal(10, progression.GetVictoryExperience("slime-field"));
        Assert.Equal(15, progression.GetVictoryExperience("goblin-camp"));
        Assert.Equal(20, progression.GetVictoryExperience("wolf-forest"));
    }
}
