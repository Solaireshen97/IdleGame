using Game.Server.Configuration;
using Game.Shared;
using Game.Shared.Models;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class SkillCatalog
{
    private readonly Dictionary<string, ProfessionOptions> _professions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CombatSkillOptions> _skills = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SkillTalentNodeOptions> _talentNodes = new(StringComparer.OrdinalIgnoreCase);

    public SkillCatalog(IOptions<SkillOptions> options, MonsterCombatCatalog? monsterCombatCatalog = null)
    {
        foreach (var profession in options.Value.Professions)
        {
            if (string.IsNullOrWhiteSpace(profession.Code) || string.IsNullOrWhiteSpace(profession.Name) ||
                profession.StartingSkills.Count > SkillRules.SlotCount || profession.RequiredLevel < 1 ||
                !_professions.TryAdd(profession.Code, profession))
                throw new InvalidOperationException($"Invalid profession configuration: {profession.Code}");
        }
        if (_professions.TryGetValue(SkillRules.DefaultProfessionCode, out var defaultProfession) && defaultProfession.IsPromotion)
            throw new InvalidOperationException("The default profession must be a base profession.");
        foreach (var profession in _professions.Values.Where(item => item.IsPromotion))
            if (profession.BaseProfessionCode is null || !_professions.TryGetValue(profession.BaseProfessionCode, out var parent) || parent.IsPromotion)
                throw new InvalidOperationException($"Invalid promotion configuration: {profession.Code}");

        foreach (var skill in options.Value.Abilities)
        {
            var effects = EffectsFor(skill);
            if (string.IsNullOrWhiteSpace(skill.Code) || string.IsNullOrWhiteSpace(skill.Name) || string.IsNullOrWhiteSpace(skill.Description) ||
                !_professions.ContainsKey(skill.ProfessionCode) || effects.Count == 0 ||
                effects.Any(effect => !IsValidEffect(effect) || effect.Type == "ApplyStatus" && monsterCombatCatalog is not null && monsterCombatCatalog.FindStatus(effect.StatusCode) is null) ||
                AutoConditionFor(skill) is not ("Always" or "LowestHpBelowThreshold" or "AllyHasDebuff" or "MonsterHasBuff" or "InterruptibleIntent") ||
                skill.CooldownRounds < 0 || !_skills.TryAdd(skill.Code, skill))
                throw new InvalidOperationException($"Invalid skill configuration: {skill.Code}");
        }
        foreach (var profession in _professions.Values)
            if (profession.StartingSkills.Count == 0 || profession.StartingSkills.Distinct(StringComparer.OrdinalIgnoreCase).Count() != profession.StartingSkills.Count ||
                profession.StartingSkills.Any(code => !_skills.TryGetValue(code, out var skill) || !string.Equals(skill.ProfessionCode, profession.Code, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Invalid starting skills for profession: {profession.Code}");

        foreach (var node in options.Value.TalentNodes)
        {
            var validSkill = node.SkillCode is null || _skills.TryGetValue(node.SkillCode, out var skill) &&
                string.Equals(skill.ProfessionCode, node.ProfessionCode, StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(node.Code) || string.IsNullOrWhiteSpace(node.Name) || string.IsNullOrWhiteSpace(node.Description) ||
                !_professions.TryGetValue(node.ProfessionCode, out var profession) || profession.IsPromotion || !validSkill ||
                node.Cost != 1 || node.MaxRank is < 1 or > 3 || node.Tier < 1 || node.Column is < 1 or > 3 ||
                node.RequiredLevel is < 1 or > SkillRules.PromotionLevel || node.RequiredTreePoints is < 0 or > 20 ||
                string.IsNullOrWhiteSpace(node.BranchCode) || !_talentNodes.TryAdd(node.Code, node))
                throw new InvalidOperationException($"Invalid talent configuration: {node.Code}");
        }
        foreach (var node in _talentNodes.Values)
            if (node.Prerequisites.Distinct(StringComparer.OrdinalIgnoreCase).Count() != node.Prerequisites.Count ||
                node.Prerequisites.Any(code => !_talentNodes.TryGetValue(code, out var parent) ||
                    !string.Equals(parent.ProfessionCode, node.ProfessionCode, StringComparison.OrdinalIgnoreCase) || parent.Tier >= node.Tier))
                throw new InvalidOperationException($"Invalid talent prerequisites: {node.Code}");

        foreach (var skill in _skills.Values)
        {
            var owner = _professions[skill.ProfessionCode];
            if (!owner.StartingSkills.Contains(skill.Code, StringComparer.OrdinalIgnoreCase) &&
                !_talentNodes.Values.Any(node => string.Equals(node.SkillCode, skill.Code, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Skill has no unlock path: {skill.Code}");
        }
    }

    public IReadOnlyCollection<ProfessionOptions> Professions => _professions.Values;
    public IReadOnlyList<ProfessionOptions> BaseProfessions => _professions.Values.Where(item => !item.IsPromotion).ToList();
    public IReadOnlyList<ProfessionOptions> PromotionsFor(string baseCode) => _professions.Values
        .Where(item => item.IsPromotion && string.Equals(item.BaseProfessionCode, baseCode, StringComparison.OrdinalIgnoreCase)).ToList();
    public ProfessionOptions? FindProfession(string? code)
    {
        if (code is not null && _professions.TryGetValue(code, out var value)) return value;
        // Keeps isolated older test/config fixtures readable while production uses the formal base professions.
        if (string.Equals(code, "swordsman", StringComparison.OrdinalIgnoreCase) && _professions.TryGetValue("knight", out value)) return value;
        if (string.Equals(code, "acolyte", StringComparison.OrdinalIgnoreCase) && _professions.TryGetValue("cleric", out value)) return value;
        return null;
    }
    public ProfessionOptions? EffectiveProfession(Character character) => FindProfession(character.AdvancedProfessionCode) ?? FindProfession(character.ProfessionCode);
    public CombatSkillOptions? FindSkill(string? code) => code is not null && _skills.TryGetValue(code, out var value) ? value : null;
    public SkillTalentNodeOptions? FindTalentNode(string? code) => code is not null && _talentNodes.TryGetValue(code, out var value) ? value : null;
    public IReadOnlyList<SkillTalentNodeOptions> TalentNodesForProfession(string professionCode) => _talentNodes.Values
        .Where(node => string.Equals(node.ProfessionCode, professionCode, StringComparison.OrdinalIgnoreCase)).OrderBy(node => node.Tier).ThenBy(node => node.Column).ToList();

    public bool IsNodeActive(Character character, SkillTalentNodeOptions node, IReadOnlyDictionary<string, int> ranks) =>
        ranks.GetValueOrDefault(node.Code) > 0 && node.RequiredLevel <= character.Level &&
        node.Prerequisites.All(code => ranks.GetValueOrDefault(code) >= (_talentNodes.TryGetValue(code, out var parent) ? parent.MaxRank : int.MaxValue));

    public bool IsLearned(Character character, string? skillCode, IReadOnlyDictionary<string, int> ranks)
    {
        var skill = FindSkill(skillCode);
        if (skill is null) return false;
        if (FindProfession(character.ProfessionCode)?.StartingSkills.Contains(skill.Code, StringComparer.OrdinalIgnoreCase) == true) return true;
        if (FindProfession(character.AdvancedProfessionCode)?.StartingSkills.Contains(skill.Code, StringComparer.OrdinalIgnoreCase) == true) return true;
        var resolvedBaseCode = FindProfession(character.ProfessionCode)?.Code ?? character.ProfessionCode;
        return _talentNodes.Values.Any(node => string.Equals(node.SkillCode, skill.Code, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(node.ProfessionCode, resolvedBaseCode, StringComparison.OrdinalIgnoreCase) && IsNodeActive(character, node, ranks));
    }

    public IReadOnlyList<CombatSkillOptions> LearnedSkills(Character character, IReadOnlyDictionary<string, int> ranks)
    {
        var codes = new List<string>();
        codes.AddRange(FindProfession(character.ProfessionCode)?.StartingSkills ?? []);
        codes.AddRange(FindProfession(character.AdvancedProfessionCode)?.StartingSkills ?? []);
        codes.AddRange(TalentNodesForProfession(character.ProfessionCode).Where(node => node.SkillCode is not null && IsNodeActive(character, node, ranks)).Select(node => node.SkillCode!));
        return codes.Distinct(StringComparer.OrdinalIgnoreCase).Select(code => _skills[code]).ToList();
    }

    public static IReadOnlyList<CombatSkillEffectOptions> EffectsFor(CombatSkillOptions skill) => skill.Effects.Count > 0 ? skill.Effects :
        string.IsNullOrWhiteSpace(skill.EffectType) ? [] : [new CombatSkillEffectOptions { Type = skill.EffectType,
            Target = skill.EffectType switch { "Damage" => "Monster", "Heal" => "LowestHpAlly", "Guard" => "FrontAlly", _ => string.Empty },
            Power = skill.Power, AttackPowerPercent = skill.AttackPowerPercent, HealMaxHpPercent = skill.HealMaxHpPercent }];
    public static string AutoConditionFor(CombatSkillOptions skill) => !string.IsNullOrWhiteSpace(skill.AutoCondition) ? skill.AutoCondition :
        EffectsFor(skill).Any(effect => effect.Type is "Heal" or "Guard") ? "LowestHpBelowThreshold" : "Always";
    public static string PrimaryEffectType(CombatSkillOptions skill) => EffectsFor(skill).First().Type;
    public static int PrimaryPower(CombatSkillOptions skill) => EffectsFor(skill).First().Power;

    private static bool IsValidEffect(CombatSkillEffectOptions effect)
    {
        if (effect.Type is not ("Damage" or "Heal" or "Guard" or "Cleanse" or "Dispel" or "Interrupt" or "ApplyStatus")) return false;
        var validTarget = effect.Type switch { "Damage" or "Dispel" or "Interrupt" => effect.Target == "Monster",
            "Heal" => effect.Target is "LowestHpAlly" or "AllAlive" or "Self", "Guard" => effect.Target == "FrontAlly",
            "Cleanse" => effect.Target is "FirstDebuffedAlly" or "Self", "ApplyStatus" => effect.Target is "Monster" or "Self" or "FrontAlly", _ => false };
        if (!validTarget || effect.Power < 0 || effect.AttackPowerPercent is < 0 or > 1000 || effect.HealMaxHpPercent is < 0 or > 100) return false;
        if (effect.Type == "Damage" && effect.Power == 0 && effect.AttackPowerPercent == 0) return false;
        if (effect.Type == "Heal" && effect.Power == 0 && effect.HealMaxHpPercent == 0) return false;
        if (effect.Type == "Guard" && effect.Power is <= 0 or > 100) return false;
        return effect.Type != "ApplyStatus" || !string.IsNullOrWhiteSpace(effect.StatusCode) && effect.DurationRounds > 0;
    }
}
