using Game.Server.Configuration;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class MonsterCombatCatalog
{
    public BattleStatusCatalog Statuses { get; }
    private readonly Dictionary<string, MonsterSkillOptions> _skills = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MonsterCombatProfileOptions> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MonsterSkillDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MonsterCombatProfile> _compiledProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int[]> _depthStages = new(StringComparer.OrdinalIgnoreCase);
    private readonly MonsterCombatOptions _source;

    public MonsterCombatCatalog(IOptions<MonsterCombatOptions> options, BattleStatusCatalog? statuses = null)
    {
        _source = Copy(options.Value);
        var content = Copy(_source);
        Statuses = statuses ?? new BattleStatusCatalog(Options.Create(_source));

        foreach (var skill in content.Skills)
        {
            if (string.IsNullOrWhiteSpace(skill.Code) || string.IsNullOrWhiteSpace(skill.Name) ||
                string.IsNullOrWhiteSpace(skill.Description) || skill.TargetType is not ("Self" or "Front" or "AllAlive") ||
                skill.DamagePowerPercent < 0 || skill.CooldownRounds < 0 || skill.InitialCooldownRounds < 0 ||
                skill.SelfHpBelowPercent is < 1 or > 100 ||
                skill.RoomRoundAtLeast is < 1 or > 250 || skill.ForcedPriority is < 0 or > 100 ||
                skill.DangerLevel is not ("Normal" or "Dangerous" or "Deadly") ||
                (skill.Effects is null ? skill.DamagePowerPercent == 0 && skill.Statuses.Count == 0 :
                    skill.DamagePowerPercent != 0 || skill.Statuses.Count != 0 || skill.Effects.Count == 0 ||
                    skill.Effects.Any(effect => !ValidEffect(effect, skill.TargetType))) ||
                skill.Statuses.Any(status => status.DurationRounds <= 0 || Statuses.Find(status.StatusCode) is null) ||
                !_skills.TryAdd(skill.Code, skill))
                throw new InvalidOperationException($"Invalid monster skill configuration: {skill.Code}");
        }

        foreach (var (code, profile) in content.Profiles)
        {
            if (string.IsNullOrWhiteSpace(code) || profile.SkillUseChancePercent is < 0 or > 100 ||
                profile.Skills.Any(skill => skill.Weight <= 0 || !_skills.ContainsKey(skill.Code)) ||
                profile.Skills.Select(skill => skill.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profile.Skills.Count ||
                !_profiles.TryAdd(code, profile))
                throw new InvalidOperationException($"Invalid monster combat profile: {code}");
        }

        foreach (var (code, stages) in _source.DepthProgressions)
        {
            if (string.IsNullOrWhiteSpace(code) || stages.Count == 0 ||
                stages.Select(stage => stage.Depth).Distinct().Count() != stages.Count ||
                stages.Any(stage => stage.Depth is < 2 or > 100 ||
                    stage.ReplacementProfileCode is not null && !_profiles.ContainsKey(stage.ReplacementProfileCode) ||
                    stage.AddedSkills.Any(skill => skill.Weight <= 0 || !_skills.ContainsKey(skill.Code))))
                throw new InvalidOperationException($"Invalid monster depth progression: {code}");
        }
        foreach (var (baseCode, baseProfile) in _profiles.ToArray())
        {
            if (baseProfile.DepthProgressionCode is null) continue;
            if (!_source.DepthProgressions.TryGetValue(baseProfile.DepthProgressionCode, out var progression))
                throw new InvalidOperationException($"Unknown monster depth progression for {baseCode}: {baseProfile.DepthProgressionCode}");
            var stages = progression.OrderBy(stage => stage.Depth).ToArray();
            _depthStages.Add(baseCode, stages.Select(stage => stage.Depth).ToArray());
            var current = CopyProfile(baseProfile);
            foreach (var stage in stages)
            {
                if (stage.ReplacementProfileCode is not null)
                    current = CopyProfile(_source.Profiles[stage.ReplacementProfileCode]);
                current.Skills.AddRange(stage.AddedSkills.Select(skill => new MonsterProfileSkillOptions
                    { Code = skill.Code, Weight = skill.Weight }));
                if (current.Skills.Select(skill => skill.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != current.Skills.Count)
                    throw new InvalidOperationException($"Duplicate skill in monster depth progression: {baseCode}, LV{stage.Depth}");
                var code = DepthProfileCode(baseCode, stage.Depth);
                if (!_profiles.TryAdd(code, CopyProfile(current)))
                    throw new InvalidOperationException($"Reserved depth profile code: {code}");
            }
        }
        foreach (var skill in _skills.Values)
            _definitions.Add(skill.Code, MonsterSkillDefinition.Compile(skill));
        foreach (var (code, profile) in _profiles)
            _compiledProfiles.Add(code, new(profile.SkillUseChancePercent,
                profile.Skills.Select(skill => new MonsterProfileSkill(skill.Code, skill.Weight)).ToImmutableArray()));
    }

    private bool ValidEffect(CombatSkillEffectOptions effect, string intentTarget)
    {
        if (effect.Target is not ("Self" or "Front" or "AllAlive") ||
            effect.Target != "Self" && effect.Target != intentTarget || effect.Power < 0 ||
            effect.AttackPowerPercent is < 0 or > 1000 || effect.HealMaxHpPercent is < 0 or > 100) return false;
        return effect.Type switch
        {
            "Damage" => effect.Target != "Self" && (effect.Power > 0 || effect.AttackPowerPercent > 0),
            "Heal" => effect.Target == "Self" && (effect.Power > 0 || effect.HealMaxHpPercent > 0),
            "Guard" => effect.Target == "Self" && effect.Power is > 0 and <= 100,
            "Cleanse" => effect.Target == "Self",
            "Dispel" => effect.Target != "Self",
            "ApplyStatus" => effect.DurationRounds > 0 && Statuses.Find(effect.StatusCode) is not null,
            _ => false
        };
    }

    public string ResolveDepthProfile(string baseProfileCode, int depth)
    {
        if (depth <= 1 || !_depthStages.TryGetValue(baseProfileCode, out var stages)) return baseProfileCode;
        var stage = stages.LastOrDefault(level => level <= depth);
        return stage == 0 ? baseProfileCode : DepthProfileCode(baseProfileCode, stage);
    }

    public IReadOnlyList<string> GetAddedMechanics(string baseProfileCode, int depth)
    {
        if (!_profiles.TryGetValue(baseProfileCode, out var original)) return [];
        var resolved = ResolveProfile(ResolveDepthProfile(baseProfileCode, depth));
        var originalCodes = original.Skills.Select(skill => skill.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return resolved is null ? [] : resolved.Skills.Where(skill => !originalCodes.Contains(skill.Code))
            .Select(skill => _definitions[skill.Code].Name).Distinct().ToArray();
    }

    // Export authored declarations, never the generated profiles, so rebuilding cannot generate twice.
    public MonsterCombatOptions ExportOptions() => Copy(_source);

    private static MonsterCombatOptions Copy(MonsterCombatOptions source)
    {
        var copy = JsonSerializer.Deserialize<MonsterCombatOptions>(JsonSerializer.Serialize(source))!;
        copy.Profiles = new(copy.Profiles, StringComparer.OrdinalIgnoreCase);
        copy.DepthProgressions = new(copy.DepthProgressions, StringComparer.OrdinalIgnoreCase);
        return copy;
    }

    private static MonsterCombatProfileOptions CopyProfile(MonsterCombatProfileOptions source) => new()
    {
        SkillUseChancePercent = source.SkillUseChancePercent,
        Skills = source.Skills.Select(skill => new MonsterProfileSkillOptions { Code = skill.Code, Weight = skill.Weight }).ToList()
    };

    private static string DepthProfileCode(string baseProfileCode, int depth) => $"{baseProfileCode}:depth-lv{depth}";

    public BattleStatusDefinition? FindStatus(string? code) => Statuses.Find(code);

    public MonsterSkillDefinition? ResolveSkill(string? code) =>
        code is not null && _definitions.TryGetValue(code, out var skill) ? skill : null;

    public MonsterCombatProfile? ResolveProfile(string? code) =>
        code is not null && _compiledProfiles.TryGetValue(code, out var profile) ? profile : null;

    public MonsterSkillOptions? FindSkill(string? code) =>
        code is not null && _skills.TryGetValue(code, out var skill) ? skill : null;

    public MonsterCombatProfileOptions? FindProfile(string? code) =>
        code is not null && _profiles.TryGetValue(code, out var profile) ? profile : null;
}
