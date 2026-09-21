using Game.Server.Configuration;
using Game.Shared.Models;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class DungeonEncounterCatalog
{
    private readonly Dictionary<string, List<DungeonWaveOptions>> _dungeons;

    public DungeonEncounterCatalog(IOptions<DungeonEncounterOptions> options, MonsterCombatCatalog? combatCatalog = null,
        RewardCatalog? rewardCatalog = null)
    {
        _dungeons = new Dictionary<string, List<DungeonWaveOptions>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, waves) in options.Value.Dungeons)
        {
            if (string.IsNullOrWhiteSpace(code) || waves.Count == 0 || waves.Any(wave => wave.Monsters.Count == 0) ||
                waves.SelectMany(wave => wave.Monsters).Any(monster => string.IsNullOrWhiteSpace(monster.Name) ||
                    monster.MaxHp <= 0 || monster.Attack < 0 || monster.Defense < 0 ||
                    !string.IsNullOrWhiteSpace(monster.CombatProfileCode) && combatCatalog?.FindProfile(monster.CombatProfileCode) is null ||
                    rewardCatalog is not null && !rewardCatalog.HasRewardProfile(
                        string.IsNullOrWhiteSpace(monster.RewardProfileCode) ? code : monster.RewardProfileCode, false)))
                throw new InvalidOperationException($"Invalid dungeon encounter: {code}");
            _dungeons.Add(code, waves);
        }
    }

    public IReadOnlyList<Monster> CreateMonsters(Dungeon dungeon)
    {
        if (!_dungeons.TryGetValue(dungeon.Code, out var waves))
            return [CreateLegacyMonster(dungeon)];

        return waves.SelectMany((wave, waveIndex) => wave.Monsters.Select((monster, monsterIndex) => new Monster
        {
            Name = monster.Name,
            Element = monster.Element,
            Hp = monster.MaxHp,
            MaxHp = monster.MaxHp,
            Attack = monster.Attack,
            Defense = monster.Defense,
            WaveNumber = waveIndex + 1,
            Position = monsterIndex + 1,
            CombatProfileCode = monster.CombatProfileCode,
            RewardProfileCode = monster.RewardProfileCode,
            IsBoss = monster.IsBoss
        })).ToList();
    }

    public int GetWaveCount(Dungeon dungeon) => _dungeons.TryGetValue(dungeon.Code, out var waves) ? waves.Count : 1;

    public int GetMonsterCount(Dungeon dungeon) => _dungeons.TryGetValue(dungeon.Code, out var waves)
        ? waves.Sum(wave => wave.Monsters.Count)
        : 1;

    public IReadOnlyList<EncounterRewardSource> GetRewardSources(Dungeon dungeon)
    {
        if (!_dungeons.TryGetValue(dungeon.Code, out var waves))
            return [new EncounterRewardSource(dungeon.Code, false)];

        return waves.SelectMany(wave => wave.Monsters)
            .Select(monster => new EncounterRewardSource(
                string.IsNullOrWhiteSpace(monster.RewardProfileCode) ? dungeon.Code : monster.RewardProfileCode,
                monster.IsBoss))
            .Distinct()
            .ToList();
    }

    private static Monster CreateLegacyMonster(Dungeon dungeon) => new()
    {
        Name = dungeon.MonsterName,
        Element = dungeon.MonsterElement,
        Hp = dungeon.MonsterMaxHp,
        MaxHp = dungeon.MonsterMaxHp,
        Attack = dungeon.MonsterAttack,
        Defense = dungeon.MonsterDefense,
        WaveNumber = 1,
        Position = 1,
        CombatProfileCode = string.Empty,
        RewardProfileCode = dungeon.Code
    };
}

public sealed record EncounterRewardSource(string Code, bool IsBoss);
