using Game.Server.Configuration;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class MonsterCombatCatalog
{
    private readonly Dictionary<string, BattleStatusOptions> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MonsterSkillOptions> _skills = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MonsterCombatProfileOptions> _profiles = new(StringComparer.OrdinalIgnoreCase);

    public MonsterCombatCatalog(IOptions<MonsterCombatOptions> options)
    {
        foreach (var status in options.Value.StatusEffects)
        {
            if (string.IsNullOrWhiteSpace(status.Code) || string.IsNullOrWhiteSpace(status.Name) ||
                string.IsNullOrWhiteSpace(status.Description) ||
                status.EffectType is not ("AttackPercent" or "ReductionPercent" or "DamageOverTime" or "HealOverTime" or "SilenceNextIntent") ||
                status.ValuePerStack == 0 && status.EffectType is not ("DamageOverTime" or "HealOverTime") ||
                status.MaxStacks is < 1 or > 10 ||
                status.Stacking is not ("RefreshDuration" or "AddStack" or "ReplaceIfStronger") ||
                !_statuses.TryAdd(status.Code, status))
                throw new InvalidOperationException($"Invalid battle status configuration: {status.Code}");
        }

        foreach (var skill in options.Value.Skills)
        {
            if (string.IsNullOrWhiteSpace(skill.Code) || string.IsNullOrWhiteSpace(skill.Name) ||
                string.IsNullOrWhiteSpace(skill.Description) || skill.TargetType is not ("Self" or "Front" or "AllAlive") ||
                skill.DamagePowerPercent < 0 || skill.CooldownRounds < 0 ||
                skill.SelfHpBelowPercent is < 1 or > 100 ||
                skill.RoomRoundAtLeast is < 1 or > 250 || skill.ForcedPriority is < 0 or > 100 ||
                skill.DangerLevel is not ("Normal" or "Dangerous" or "Deadly") ||
                skill.DamagePowerPercent == 0 && skill.Statuses.Count == 0 ||
                skill.Statuses.Any(status => status.DurationRounds <= 0 || !_statuses.ContainsKey(status.StatusCode)) ||
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
    }

    public string ResolveDepthProfile(string baseProfileCode, int depth) => depth <= 1
        ? baseProfileCode
        : _profiles.ContainsKey(DepthProfileCode(baseProfileCode, Math.Min(depth, 4)))
            ? DepthProfileCode(baseProfileCode, Math.Min(depth, 4))
            : baseProfileCode;

    private static string DepthProfileCode(string baseProfileCode, int depth) => $"{baseProfileCode}:depth-lv{depth}";

    public BattleStatusOptions? FindStatus(string? code) =>
        code is not null && _statuses.TryGetValue(code, out var status) ? status : null;

    public MonsterSkillOptions? FindSkill(string? code) =>
        code is not null && _skills.TryGetValue(code, out var skill) ? skill : null;

    public MonsterCombatProfileOptions? FindProfile(string? code) =>
        code is not null && _profiles.TryGetValue(code, out var profile) ? profile : null;
}
