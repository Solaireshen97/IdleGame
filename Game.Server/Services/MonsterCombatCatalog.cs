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
                status.EffectType is not ("AttackPercent" or "ReductionPercent" or "DamageOverTime" or "SilenceNextIntent") ||
                status.ValuePerStack == 0 || status.MaxStacks is < 1 or > 10 ||
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
    }

    public BattleStatusOptions? FindStatus(string? code) =>
        code is not null && _statuses.TryGetValue(code, out var status) ? status : null;

    public MonsterSkillOptions? FindSkill(string? code) =>
        code is not null && _skills.TryGetValue(code, out var skill) ? skill : null;

    public MonsterCombatProfileOptions? FindProfile(string? code) =>
        code is not null && _profiles.TryGetValue(code, out var profile) ? profile : null;
}
