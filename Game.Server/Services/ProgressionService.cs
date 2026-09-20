using Game.Server.Configuration;
using Game.Shared.Models;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class ProgressionService
{
    private readonly ProgressionOptions _settings;

    public ProgressionService(IOptions<ProgressionOptions> options)
    {
        _settings = options.Value;
        if (_settings.MaximumLevel < 2 ||
            _settings.ExperienceToNextLevel.Count != _settings.MaximumLevel - 1 ||
            _settings.ExperienceToNextLevel.Any(required => required <= 0) ||
            _settings.DefaultVictoryExperience <= 0 ||
            _settings.DungeonVictoryExperience.Values.Any(reward => reward <= 0))
            throw new InvalidOperationException("Progression settings must define positive experience requirements and rewards for every level.");
    }

    public int? GetExperienceToNextLevel(int level) =>
        level >= _settings.MaximumLevel ? null : _settings.ExperienceToNextLevel[level - 1];

    public int GetVictoryExperience(string dungeonCode) =>
        _settings.DungeonVictoryExperience.TryGetValue(dungeonCode, out var reward)
            ? reward : _settings.DefaultVictoryExperience;

    public ProgressionGain AwardVictoryExperience(Character character, int reward)
    {
        if (character.Level >= _settings.MaximumLevel) return new ProgressionGain(0, 0);

        character.Experience = checked(character.Experience + reward);
        var levelsGained = 0;
        while (character.Level < _settings.MaximumLevel &&
            character.Experience >= _settings.ExperienceToNextLevel[character.Level - 1])
        {
            character.Experience -= _settings.ExperienceToNextLevel[character.Level - 1];
            character.Level++;
            character.TalentPoints++;
            levelsGained++;
        }

        if (character.Level == _settings.MaximumLevel) character.Experience = 0;
        return new ProgressionGain(reward, levelsGained);
    }
}

public readonly record struct ProgressionGain(int ExperienceGained, int LevelsGained);
