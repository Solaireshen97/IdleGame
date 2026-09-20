using Game.Server.Configuration;
using Game.Server.Services;
using Microsoft.Extensions.Options;

namespace Game.Server.Tests;

internal static class ProgressionTestFactory
{
    public static ProgressionService Create(int slimeReward = 10) => new(Options.Create(new ProgressionOptions
    {
        MaximumLevel = 10,
        ExperienceToNextLevel = [20, 30, 40, 50, 60, 70, 80, 90, 100],
        DefaultVictoryExperience = 10,
        DungeonVictoryExperience = new Dictionary<string, int> { ["slime-field"] = slimeReward, ["goblin-camp"] = 15, ["wolf-forest"] = 20 }
    }));
}
