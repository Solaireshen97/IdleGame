using Game.Server.Configuration;
using Game.Shared;
using Game.Shared.Models;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class SkillCatalog
{
    private readonly Dictionary<string, ProfessionOptions> _professions;
    private readonly Dictionary<string, CombatSkillOptions> _skills;
    private readonly Dictionary<string, SkillTalentNodeOptions> _talentNodes;

    public SkillCatalog(IOptions<SkillOptions> options, MonsterCombatCatalog? monsterCombatCatalog = null)
    {
        var settings = options.Value;
        _professions = new(StringComparer.OrdinalIgnoreCase);
        _skills = new(StringComparer.OrdinalIgnoreCase);
        _talentNodes = new(StringComparer.OrdinalIgnoreCase);
        foreach (var profession in settings.Professions)
        {
            if (string.IsNullOrWhiteSpace(profession.Code) || string.IsNullOrWhiteSpace(profession.Name) ||
                profession.StartingSkills.Count > SkillRules.SlotCount || !_professions.TryAdd(profession.Code, profession))
                throw new InvalidOperationException($"Invalid profession configuration: {profession.Code}");
        }
        if (!_professions.ContainsKey(SkillRules.DefaultProfessionCode))
            throw new InvalidOperationException("The default profession must be configured.");

        foreach (var skill in settings.Abilities)
        {
            var effects = EffectsFor(skill);
            if (string.IsNullOrWhiteSpace(skill.Code) || string.IsNullOrWhiteSpace(skill.Name) ||
                string.IsNullOrWhiteSpace(skill.Description) || !_professions.ContainsKey(skill.ProfessionCode) ||
                effects.Count == 0 || effects.Any(effect => !IsValidEffect(effect) ||
                    effect.Type == "ApplyStatus" && monsterCombatCatalog is not null &&
                    monsterCombatCatalog.FindStatus(effect.StatusCode) is null) ||
                AutoConditionFor(skill) is not ("Always" or "LowestHpBelowThreshold" or "AllyHasDebuff" or
                    "MonsterHasBuff" or "InterruptibleIntent") ||
                skill.CooldownRounds < 0 || !_skills.TryAdd(skill.Code, skill))
                throw new InvalidOperationException($"Invalid skill configuration: {skill.Code}");
        }

        foreach (var profession in _professions.Values)
        {
            if (profession.StartingSkills.Count == 0 ||
                profession.StartingSkills.Distinct(StringComparer.OrdinalIgnoreCase).Count() != profession.StartingSkills.Count ||
                profession.StartingSkills.Any(code => !_skills.TryGetValue(code, out var skill) ||
                    !string.Equals(skill.ProfessionCode, profession.Code, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Invalid starting skills for profession: {profession.Code}");
        }

        foreach (var node in settings.TalentNodes)
        {
            if (string.IsNullOrWhiteSpace(node.Code) || string.IsNullOrWhiteSpace(node.Name) ||
                string.IsNullOrWhiteSpace(node.Description) || !_professions.ContainsKey(node.ProfessionCode) ||
                !_skills.TryGetValue(node.SkillCode, out var skill) ||
                !string.Equals(skill.ProfessionCode, node.ProfessionCode, StringComparison.OrdinalIgnoreCase) ||
                _professions[node.ProfessionCode].StartingSkills.Contains(node.SkillCode, StringComparer.OrdinalIgnoreCase) ||
                node.Cost <= 0 || node.Tier < 1 || node.Column is < 1 or > 3 ||
                !Enum.IsDefined(node.RequiredTalentType) || node.RequiredTalentRank is < 1 or > TalentRules.MaxRank ||
                node.Column != (int)node.RequiredTalentType + 1 ||
                !_talentNodes.TryAdd(node.Code, node))
                throw new InvalidOperationException($"Invalid skill talent configuration: {node.Code}");
        }

        foreach (var node in _talentNodes.Values)
        {
            if (node.Prerequisites.Distinct(StringComparer.OrdinalIgnoreCase).Count() != node.Prerequisites.Count ||
                node.Prerequisites.Any(code => !_talentNodes.TryGetValue(code, out var parent) ||
                    !string.Equals(parent.ProfessionCode, node.ProfessionCode, StringComparison.OrdinalIgnoreCase) ||
                    parent.Tier >= node.Tier) ||
                _talentNodes.Values.Any(other => other != node &&
                    string.Equals(other.ProfessionCode, node.ProfessionCode, StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(other.SkillCode, node.SkillCode, StringComparison.OrdinalIgnoreCase) ||
                     other.Tier == node.Tier && other.Column == node.Column)))
                throw new InvalidOperationException($"Invalid prerequisites or tree position: {node.Code}");
        }

        foreach (var skill in _skills.Values)
        {
            if (!_professions[skill.ProfessionCode].StartingSkills.Contains(skill.Code, StringComparer.OrdinalIgnoreCase) &&
                !_talentNodes.Values.Any(node => string.Equals(node.SkillCode, skill.Code, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Skill has no unlock path: {skill.Code}");
        }
    }

    public IReadOnlyCollection<ProfessionOptions> Professions => _professions.Values;
    public ProfessionOptions? FindProfession(string? code) =>
        code is not null && _professions.TryGetValue(code, out var profession) ? profession : null;
    public CombatSkillOptions? FindSkill(string? code) =>
        code is not null && _skills.TryGetValue(code, out var skill) ? skill : null;
    public static IReadOnlyList<CombatSkillEffectOptions> EffectsFor(CombatSkillOptions skill) =>
        skill.Effects.Count > 0
            ? skill.Effects
            : string.IsNullOrWhiteSpace(skill.EffectType)
                ? []
                : [new CombatSkillEffectOptions
                {
                    Type = skill.EffectType,
                    Target = skill.EffectType switch
                    {
                        "Damage" => "Monster",
                        "Heal" => "LowestHpAlly",
                        "Guard" => "FrontAlly",
                        _ => string.Empty
                    },
                    Power = skill.Power
                }];
    public static string AutoConditionFor(CombatSkillOptions skill) =>
        !string.IsNullOrWhiteSpace(skill.AutoCondition) ? skill.AutoCondition :
        EffectsFor(skill).Any(effect => effect.Type is "Heal" or "Guard") ? "LowestHpBelowThreshold" : "Always";
    public static string PrimaryEffectType(CombatSkillOptions skill) => EffectsFor(skill).First().Type;
    public static int PrimaryPower(CombatSkillOptions skill) => EffectsFor(skill).First().Power;
    public SkillTalentNodeOptions? FindTalentNode(string? code) =>
        code is not null && _talentNodes.TryGetValue(code, out var node) ? node : null;
    public IReadOnlyList<SkillTalentNodeOptions> TalentNodesForProfession(string professionCode) =>
        _talentNodes.Values.Where(node => string.Equals(node.ProfessionCode, professionCode, StringComparison.OrdinalIgnoreCase))
            .OrderBy(node => node.Tier).ThenBy(node => node.Column).ToList();

    public bool IsLearned(Game.Shared.Models.Character character, string? skillCode, IReadOnlySet<string> purchasedNodes)
    {
        var profession = FindProfession(character.ProfessionCode);
        var skill = FindSkill(skillCode);
        if (profession is null || skill is null ||
            !string.Equals(skill.ProfessionCode, profession.Code, StringComparison.OrdinalIgnoreCase)) return false;
        return profession.StartingSkills.Contains(skill.Code, StringComparer.OrdinalIgnoreCase) ||
               _talentNodes.Values.Any(node => string.Equals(node.SkillCode, skill.Code, StringComparison.OrdinalIgnoreCase) &&
                   IsNodeActive(character, node, purchasedNodes));
    }

    public IReadOnlyList<CombatSkillOptions> LearnedSkills(Game.Shared.Models.Character character, IReadOnlySet<string> purchasedNodes)
    {
        var profession = FindProfession(character.ProfessionCode);
        if (profession is null) return [];
        return profession.StartingSkills.Select(code => _skills[code])
            .Concat(TalentNodesForProfession(profession.Code).Where(node => IsNodeActive(character, node, purchasedNodes))
                .Select(node => _skills[node.SkillCode])).ToList();
    }

    public bool IsNodeActive(Character character, SkillTalentNodeOptions node, IReadOnlySet<string> purchasedNodes) =>
        purchasedNodes.Contains(node.Code) &&
        TalentRules.GetRank(character, node.RequiredTalentType) >= node.RequiredTalentRank &&
        node.Prerequisites.All(code => _talentNodes.TryGetValue(code, out var parent) &&
            IsNodeActive(character, parent, purchasedNodes));

    private static bool IsValidEffect(CombatSkillEffectOptions effect)
    {
        if (effect.Type is not ("Damage" or "Heal" or "Guard" or "Cleanse" or "Dispel" or "Interrupt" or "ApplyStatus"))
            return false;
        var validTarget = effect.Type switch
        {
            "Damage" or "Dispel" or "Interrupt" => effect.Target == "Monster",
            "Heal" => effect.Target is "LowestHpAlly" or "Self",
            "Guard" => effect.Target == "FrontAlly",
            "Cleanse" => effect.Target is "FirstDebuffedAlly" or "Self",
            "ApplyStatus" => effect.Target is "Monster" or "Self" or "FrontAlly",
            _ => false
        };
        if (!validTarget) return false;
        if (effect.Type is "Damage" or "Heal" or "Guard" && effect.Power <= 0) return false;
        if (effect.Type == "Guard" && effect.Power > 100) return false;
        return effect.Type != "ApplyStatus" ||
               !string.IsNullOrWhiteSpace(effect.StatusCode) && effect.DurationRounds > 0;
    }
}
