using Game.Server.Configuration;
using System.Collections.Immutable;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class MonsterCombatCatalog
{
    public BattleStatusCatalog Statuses { get; }
    private readonly Dictionary<string, MonsterSkillOptions> _skills = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MonsterCombatProfileOptions> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MonsterSkillDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MonsterCombatProfile> _compiledProfiles = new(StringComparer.OrdinalIgnoreCase);

    public MonsterCombatCatalog(IOptions<MonsterCombatOptions> options, BattleStatusCatalog? statuses = null)
    {
        Statuses = statuses ?? new BattleStatusCatalog(options);

        foreach (var skill in options.Value.Skills)
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

        foreach (var (code, profile) in options.Value.Profiles)
        {
            if (string.IsNullOrWhiteSpace(code) || profile.SkillUseChancePercent is < 0 or > 100 ||
                profile.Skills.Any(skill => skill.Weight <= 0 || !_skills.ContainsKey(skill.Code)) ||
                profile.Skills.Select(skill => skill.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profile.Skills.Count ||
                !_profiles.TryAdd(code, profile))
                throw new InvalidOperationException($"Invalid monster combat profile: {code}");
        }

        // Temporary attacks exercise cumulative depth loading; the themed mechanics are not implemented yet.
        string[] names = ["深层核心试击（占位）", "深层压力试击（占位）", "深层循环试击（占位）"];
        for (var depth = 2; depth <= 4; depth++)
        {
            var code = $"depth-placeholder-lv{depth}";
            if (!_skills.TryAdd(code, new MonsterSkillOptions
                {
                    Code = code,
                    Name = names[depth - 2],
                    Description = $"LV{depth} 逐层装载验证：对前排造成101%攻击伤害。真实核心、压力及循环机制尚未实现。",
                    DamagePowerPercent = 101,
                    CooldownRounds = 3,
                    ForcedPriority = 102 - depth
                }))
                throw new InvalidOperationException($"Reserved depth skill code: {code}");
        }
        foreach (var (baseCode, baseProfile) in _profiles.ToArray())
        {
            for (var depth = 2; depth <= 4; depth++)
            {
                var code = DepthProfileCode(baseCode, depth);
                if (!_profiles.TryAdd(code, new MonsterCombatProfileOptions
                    {
                        SkillUseChancePercent = baseProfile.SkillUseChancePercent,
                        Skills = baseProfile.Skills.Select(skill => new MonsterProfileSkillOptions
                            { Code = skill.Code, Weight = skill.Weight })
                            .Concat(Enumerable.Range(2, depth - 1).Select(level => new MonsterProfileSkillOptions
                                { Code = $"depth-placeholder-lv{level}" })).ToList()
                    }))
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

    public string ResolveDepthProfile(string baseProfileCode, int depth) => depth <= 1
        ? baseProfileCode
        : _profiles.ContainsKey(DepthProfileCode(baseProfileCode, Math.Min(depth, 4)))
            ? DepthProfileCode(baseProfileCode, Math.Min(depth, 4))
            : baseProfileCode;

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
