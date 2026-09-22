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
            _settings.ExperienceToNextLevel.Any(required => required <= 0))
            throw new InvalidOperationException("Progression settings must define positive experience requirements for every level.");
        if (_settings.ExperiencePercentByLevelDifference.Count == 0 ||
            _settings.ExperiencePercentByLevelDifference[0] != 100 ||
            _settings.ExperiencePercentByLevelDifference.Any(percent => percent is < 0 or > 100) ||
            _settings.ExperiencePercentByLevelDifference.Zip(_settings.ExperiencePercentByLevelDifference.Skip(1))
                .Any(pair => pair.Second > pair.First))
            throw new InvalidOperationException("Progression experience modifiers must begin at 100 and decrease by level difference.");
    }

    public int? GetExperienceToNextLevel(int level) =>
        level >= _settings.MaximumLevel ? null : _settings.ExperienceToNextLevel[level - 1];

    public int ApplyDungeonExperienceModifier(int reward, int characterLevel, int dungeonMinimumLevel)
    {
        if (reward < 0) throw new ArgumentOutOfRangeException(nameof(reward));
        if (reward == 0 || characterLevel >= _settings.MaximumLevel) return 0;
        var levelDifference = Math.Max(0, characterLevel - Math.Max(1, dungeonMinimumLevel));
        var index = Math.Min(levelDifference, _settings.ExperiencePercentByLevelDifference.Count - 1);
        return checked((int)((long)reward * _settings.ExperiencePercentByLevelDifference[index] / 100));
    }

    public ProgressionGain AwardExperience(Character character, int reward)
    {
        if (reward < 0) throw new ArgumentOutOfRangeException(nameof(reward));
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
