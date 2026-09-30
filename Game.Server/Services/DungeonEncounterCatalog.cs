using Game.Server.Configuration;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class DungeonEncounterCatalog
{
    private readonly Dictionary<string, List<DungeonWaveOptions>> _dungeons;
    private readonly DungeonDepthCatalog? _depthCatalog;
    private readonly MonsterCombatCatalog? _combatCatalog;
    private readonly Dictionary<string, DungeonRewardEligibility> _rewardEligibility;

    public DungeonEncounterCatalog(IOptions<DungeonEncounterOptions> options, MonsterCombatCatalog? combatCatalog = null,
        RewardCatalog? rewardCatalog = null, DungeonDepthCatalog? depthCatalog = null)
    {
        _depthCatalog = depthCatalog;
        _combatCatalog = combatCatalog;
        _rewardEligibility = new(options.Value.RewardEligibility, StringComparer.OrdinalIgnoreCase);
        _dungeons = new Dictionary<string, List<DungeonWaveOptions>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, waves) in options.Value.Dungeons)
        {
            if (string.IsNullOrWhiteSpace(code) || waves.Count == 0 || waves.Any(wave => wave.Monsters.Count == 0) ||
                waves.SelectMany(wave => wave.Monsters).Any(monster => string.IsNullOrWhiteSpace(monster.Name) ||
                    monster.MaxHp <= 0 || monster.Attack < 0 || monster.Defense < 0 ||
                    !Enum.IsDefined(monster.Element) ||
                    !string.IsNullOrWhiteSpace(monster.CombatProfileCode) && combatCatalog?.FindProfile(monster.CombatProfileCode) is null ||
                    rewardCatalog is not null && !rewardCatalog.HasRewardProfile(
                        string.IsNullOrWhiteSpace(monster.RewardProfileCode) ? code : monster.RewardProfileCode, false)))
                throw new InvalidOperationException($"Invalid dungeon encounter: {code}");
            foreach (var monster in waves.SelectMany(wave => wave.Monsters))
                depthCatalog?.ValidateStats(code, monster.MaxHp, monster.Attack, monster.Name);
            _dungeons.Add(code, waves);
        }
        foreach (var code in depthCatalog?.DungeonCodes ?? [])
            if (!_dungeons.ContainsKey(code))
                throw new InvalidOperationException($"Invalid dungeon depth configuration: {code}; a configured encounter is required to validate depth stats.");
        foreach (var (code, eligibility) in _rewardEligibility)
            if (!_dungeons.ContainsKey(code) || !Enum.IsDefined(eligibility))
                throw new InvalidOperationException($"Invalid dungeon reward eligibility: {code}");
    }

    public bool HasDefinition(string code) => _dungeons.ContainsKey(code);

    public IReadOnlyCollection<string> DungeonCodes => _dungeons.Keys;

    public DungeonRewardEligibility ResolveRewardEligibility(string dungeonCode, DungeonRewardEligibility fallback) =>
        _rewardEligibility.GetValueOrDefault(dungeonCode, fallback);

    public Monster GetRepresentativeMonster(Dungeon dungeon, int depth = 1)
    {
        if (!(_depthCatalog?.ValidateDepth(dungeon.Code, depth) ?? depth == 1))
            throw new ArgumentOutOfRangeException(nameof(depth));
        if (_dungeons.TryGetValue(dungeon.Code, out var waves))
            return CreateMonster(dungeon.Code, waves[^1].Monsters[^1], waves.Count, waves[^1].Monsters.Count, depth);
        var legacy = CreateLegacyMonster(dungeon);
        legacy.Hp = legacy.MaxHp = legacy.BaseMaxHp = _depthCatalog?.ScaleStat(legacy.MaxHp, depth, dungeon.Code) ?? legacy.MaxHp;
        legacy.Attack = _depthCatalog?.ScaleStat(legacy.Attack, depth, dungeon.Code) ?? legacy.Attack;
        return legacy;
    }

    public IReadOnlyList<string> GetAddedMechanics(Dungeon dungeon, int depth)
    {
        if (!(_depthCatalog?.ValidateDepth(dungeon.Code, depth) ?? depth == 1))
            throw new ArgumentOutOfRangeException(nameof(depth));
        if (_combatCatalog is null || !_dungeons.TryGetValue(dungeon.Code, out var waves)) return [];
        return waves.SelectMany(wave => wave.Monsters).Where(monster => monster.IsBoss)
            .SelectMany(monster => _combatCatalog.GetAddedMechanics(monster.CombatProfileCode, depth)).Distinct().ToArray();
    }

    public void PopulateRepresentativeStats(Dungeon dungeon)
    {
        // Legacy admission identifies these dungeons by the old summary monster name.
        if (dungeon.DungeonKind == "Legacy" || !HasDefinition(dungeon.Code)) return;
        var representative = GetRepresentativeMonster(dungeon);
        dungeon.MonsterName = representative.Name;
        dungeon.MonsterElement = representative.Element;
        dungeon.MonsterMaxHp = representative.MaxHp;
        dungeon.MonsterAttack = representative.Attack;
        dungeon.MonsterDefense = representative.Defense;
    }

    public IReadOnlyList<Monster> CreateMonsters(Dungeon dungeon, int depth = 1)
    {
        if (!(_depthCatalog?.ValidateDepth(dungeon.Code, depth) ?? depth == 1))
            throw new ArgumentOutOfRangeException(nameof(depth));
        if (!_dungeons.TryGetValue(dungeon.Code, out var waves))
        {
            var legacy = CreateLegacyMonster(dungeon);
            legacy.Hp = legacy.MaxHp = legacy.BaseMaxHp = Scale(legacy.MaxHp);
            legacy.Attack = Scale(legacy.Attack);
            return [legacy];
        }

        int Scale(int value) => _depthCatalog?.ScaleStat(value, depth, dungeon.Code) ?? value;

        return waves.SelectMany((wave, waveIndex) => wave.Monsters.Select((monster, monsterIndex) =>
            CreateMonster(dungeon.Code, monster, waveIndex + 1, monsterIndex + 1, depth))).ToList();
    }

    private Monster CreateMonster(string dungeonCode, EncounterMonsterOptions monster, int waveNumber, int position, int depth)
    {
        int Scale(int value) => _depthCatalog?.ScaleStat(value, depth, dungeonCode) ?? value;
        var hp = Scale(monster.MaxHp);
        return new Monster
        {
            Name = monster.Name,
            Element = monster.Element,
            Hp = hp,
            BaseMaxHp = hp,
            MaxHp = hp,
            Attack = Scale(monster.Attack),
            Defense = monster.Defense,
            WaveNumber = waveNumber,
            Position = position,
            CombatProfileCode = monster.IsBoss && depth > 1
                ? _combatCatalog?.ResolveDepthProfile(monster.CombatProfileCode, depth) ?? monster.CombatProfileCode
                : monster.CombatProfileCode,
            RewardProfileCode = monster.RewardProfileCode,
            IsBoss = monster.IsBoss
        };
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
        BaseMaxHp = dungeon.MonsterMaxHp,
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
