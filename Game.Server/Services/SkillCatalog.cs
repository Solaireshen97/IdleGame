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

        foreach (var skill in options.Value.Abilities)
        {
            var effects = EffectsFor(skill);
            var hasDamage = effects.Any(effect => effect.Type == "Damage");
            var hasConditionalDamage = skill.ConditionalDamageBonusPercent > 0;
            if (string.IsNullOrWhiteSpace(skill.Code) || string.IsNullOrWhiteSpace(skill.Name) || string.IsNullOrWhiteSpace(skill.Description) ||
                !_professions.ContainsKey(skill.ProfessionCode) || effects.Count == 0 ||
                effects.Any(effect => !IsValidEffect(effect) || effect.Type == "ApplyStatus" && monsterCombatCatalog is not null && monsterCombatCatalog.FindStatus(effect.StatusCode) is null) ||
                skill.ConditionalDamageBonusPercent is < 0 or > 200 ||
                skill.TargetHpBelowPercent is < 1 or > 100 ||
                hasConditionalDamage != (skill.RequiredTargetStatusCode is not null || skill.TargetHpBelowPercent is not null) ||
                hasConditionalDamage && !hasDamage ||
                skill.RequiredTargetStatusCode is not null && monsterCombatCatalog is not null && monsterCombatCatalog.FindStatus(skill.RequiredTargetStatusCode) is null ||
                AutoConditionFor(skill) is not ("Always" or "LowestHpBelowThreshold" or "AllyHasDebuff" or "MonsterHasBuff" or "InterruptibleIntent") ||
                skill.CooldownRounds < 0 || skill.InitialCooldownRounds < 0 || !_skills.TryAdd(skill.Code, skill))
                throw new InvalidOperationException($"Invalid skill configuration: {skill.Code}");
        }
        foreach (var group in _skills.Values.GroupBy(skill => skill.ProfessionCode, StringComparer.OrdinalIgnoreCase))
        {
            var index = 0;
            foreach (var skill in group)
            {
                skill.UnlockLevel = skill.UnlockLevel == 0 ?
                    _professions[group.Key].StartingSkills.Contains(skill.Code, StringComparer.OrdinalIgnoreCase) ? 1 :
                    Math.Min(10, 1 + index * 2) : skill.UnlockLevel;
                skill.Level2UnlockLevel = skill.Level2UnlockLevel == 0 ? 20 : skill.Level2UnlockLevel;
                skill.Level3UnlockLevel = skill.Level3UnlockLevel == 0 ? 30 : skill.Level3UnlockLevel;
                if (skill.UnlockLevel is < 1 or > 10 || skill.Level2UnlockLevel is < 11 or > 20 ||
                    skill.Level3UnlockLevel is < 21 or > 30 ||
                    !ValidVariant(skill, skill.Level2) || !ValidVariant(skill, skill.Level3) || !ValidVariant(skill, skill.SharedVersion))
                    throw new InvalidOperationException($"Invalid skill progression: {skill.Code}");
                index++;
            }
        }
        foreach (var profession in _professions.Values)
            if (profession.StartingSkills.Count == 0 || profession.StartingSkills.Distinct(StringComparer.OrdinalIgnoreCase).Count() != profession.StartingSkills.Count ||
                profession.StartingSkills.Any(code => !_skills.TryGetValue(code, out var skill) || !string.Equals(skill.ProfessionCode, profession.Code, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Invalid starting skills for profession: {profession.Code}");
        foreach (var profession in _professions.Values.Where(item => !item.IsPromotion))
        {
            profession.SharedSkillCode ??= _skills.Values.LastOrDefault(skill =>
                string.Equals(skill.ProfessionCode, profession.Code, StringComparison.OrdinalIgnoreCase))?.Code;
            if (profession.SharedSkillCode is not null && (!_skills.TryGetValue(profession.SharedSkillCode, out var shared) ||
                !string.Equals(shared.ProfessionCode, profession.Code, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Invalid shared skill: {profession.Code}");
        }

        // Legacy battle formulas still read two former node values. No talent tree is active.
        foreach (var node in options.Value.TalentNodes)
            if (!string.IsNullOrWhiteSpace(node.Code)) _talentNodes.TryAdd(node.Code, node);

    }

    public IReadOnlyCollection<ProfessionOptions> Professions => _professions.Values;
    public IReadOnlyList<ProfessionOptions> BaseProfessions => _professions.Values.Where(item => !item.IsPromotion).ToList();
    public ProfessionOptions? FindProfession(string? code)
    {
        if (code is not null && _professions.TryGetValue(code, out var value)) return value;
        // Keeps isolated older test/config fixtures readable while production uses the formal base professions.
        if (string.Equals(code, "swordsman", StringComparison.OrdinalIgnoreCase) && _professions.TryGetValue("knight", out value)) return value;
        if (string.Equals(code, "acolyte", StringComparison.OrdinalIgnoreCase) && _professions.TryGetValue("cleric", out value)) return value;
        return null;
    }
    public ProfessionOptions? EffectiveProfession(Character character) => FindProfession(character.ProfessionCode);
    public CombatSkillOptions? FindSkill(string? code) => code is not null && _skills.TryGetValue(code, out var value) ? value : null;
    public SkillTalentNodeOptions? FindTalentNode(string? code) => code is not null && _talentNodes.TryGetValue(code, out var value) ? value : null;

    public bool IsLearned(Character character, string? skillCode, IReadOnlyDictionary<string, int> ranks,
        IReadOnlyDictionary<string, int>? professionLevels = null) =>
        ResolveSkillForLevel(character, skillCode, professionLevels) is not null;

    public CombatSkillOptions? ResolveSkillForLevel(Character character, string? skillCode,
        IReadOnlyDictionary<string, int>? professionLevels = null)
    {
        var skill = FindSkill(skillCode);
        if (skill is null) return null;
        var currentProfession = FindProfession(character.ProfessionCode)?.Code ?? character.ProfessionCode;
        if (string.Equals(skill.ProfessionCode, currentProfession, StringComparison.OrdinalIgnoreCase))
        {
            if (character.Level < skill.UnlockLevel) return null;
            var rank = RankFor(skill, character.Level);
            return ApplyVariant(skill, rank == 3 ? skill.Level3 : rank == 2 ? skill.Level2 : null);
        }
        var source = FindProfession(skill.ProfessionCode);
        if (character.Level < SkillRules.SharedSkillEquipLevel || source?.SharedSkillCode is null ||
            !string.Equals(source.SharedSkillCode, skill.Code, StringComparison.OrdinalIgnoreCase) ||
            professionLevels?.GetValueOrDefault(source.Code) < SkillRules.SharedSkillUnlockLevel) return null;
        return ApplyVariant(ApplyVariant(skill, skill.Level3), skill.SharedVersion);
    }

    public IReadOnlyList<CombatSkillOptions> LearnedSkills(Character character, IReadOnlyDictionary<string, int> ranks,
        IReadOnlyDictionary<string, int>? professionLevels = null)
    {
        return _skills.Values.Where(skill => string.Equals(skill.ProfessionCode, character.ProfessionCode, StringComparison.OrdinalIgnoreCase))
            .Select(skill => ResolveSkillForLevel(character, skill.Code, professionLevels)).OfType<CombatSkillOptions>().ToList();
    }

    public IReadOnlyList<CombatSkillOptions> SkillsForProfessionAtLevel(string professionCode, int level) =>
        _skills.Values.Where(skill => string.Equals(skill.ProfessionCode, professionCode, StringComparison.OrdinalIgnoreCase) &&
            level >= skill.UnlockLevel).Select(skill => ApplyVariant(skill,
                RankFor(skill, level) == 3 ? skill.Level3 : RankFor(skill, level) == 2 ? skill.Level2 : null)).ToList();

    public IReadOnlyList<CombatSkillOptions> SharedSkills(Character character, IReadOnlyDictionary<string, int> professionLevels) =>
        _professions.Values.Where(profession => !profession.IsPromotion && profession.SharedSkillCode is not null &&
            !string.Equals(profession.Code, character.ProfessionCode, StringComparison.OrdinalIgnoreCase) &&
            professionLevels.GetValueOrDefault(profession.Code) >= SkillRules.SharedSkillUnlockLevel)
            .Select(profession => ResolveSkillForLevel(character, profession.SharedSkillCode, professionLevels) ??
                ApplyVariant(ApplyVariant(_skills[profession.SharedSkillCode!], _skills[profession.SharedSkillCode!].Level3),
                    _skills[profession.SharedSkillCode!].SharedVersion))
            .ToList();

    public static int RankFor(CombatSkillOptions skill, int level) => level >= skill.Level3UnlockLevel ? 3 :
        level >= skill.Level2UnlockLevel ? 2 : 1;

    private static CombatSkillOptions ApplyVariant(CombatSkillOptions skill, CombatSkillVariantOptions? variant)
    {
        if (variant is null) return skill;
        return new CombatSkillOptions
        {
            Code = skill.Code, ProfessionCode = skill.ProfessionCode, Name = variant.Name ?? skill.Name,
            Description = variant.Description ?? skill.Description, EffectType = skill.EffectType,
            Power = variant.Power ?? skill.Power, AttackPowerPercent = variant.AttackPowerPercent ?? skill.AttackPowerPercent,
            HealMaxHpPercent = variant.HealMaxHpPercent ?? skill.HealMaxHpPercent,
            CooldownRounds = variant.CooldownRounds ?? skill.CooldownRounds,
            InitialCooldownRounds = variant.InitialCooldownRounds ?? skill.InitialCooldownRounds,
            ConditionalDamageBonusPercent = variant.ConditionalDamageBonusPercent ?? skill.ConditionalDamageBonusPercent,
            RequiredTargetStatusCode = variant.RequiredTargetStatusCode ?? skill.RequiredTargetStatusCode,
            TargetHpBelowPercent = variant.TargetHpBelowPercent ?? skill.TargetHpBelowPercent,
            AutoCondition = variant.AutoCondition ?? skill.AutoCondition, Effects = variant.Effects ?? skill.Effects,
            UnlockLevel = skill.UnlockLevel, Level2UnlockLevel = skill.Level2UnlockLevel,
            Level3UnlockLevel = skill.Level3UnlockLevel, Level2 = skill.Level2, Level3 = skill.Level3,
            SharedVersion = skill.SharedVersion
        };
    }

    private static bool ValidVariant(CombatSkillOptions skill, CombatSkillVariantOptions? variant)
    {
        if (variant is null) return true;
        var resolved = ApplyVariant(skill, variant);
        return resolved.CooldownRounds >= 0 && resolved.InitialCooldownRounds >= 0 &&
            resolved.AttackPowerPercent is >= 0 and <= 1000 && resolved.HealMaxHpPercent is >= 0 and <= 100 &&
            resolved.ConditionalDamageBonusPercent is >= 0 and <= 200 &&
            resolved.TargetHpBelowPercent is null or >= 1 and <= 100 &&
            AutoConditionFor(resolved) is "Always" or "LowestHpBelowThreshold" or "AllyHasDebuff" or "MonsterHasBuff" or "InterruptibleIntent" &&
            (variant.Effects is null || variant.Effects.Count > 0 && variant.Effects.All(IsValidEffect));
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
        if (effect.Type is not ("Damage" or "Heal" or "Guard" or "Cleanse" or "Dispel" or "Interrupt" or "ApplyStatus" or "CooldownReduction")) return false;
        var validTarget = effect.Type switch { "Damage" or "Dispel" or "Interrupt" => effect.Target == "Monster",
            "Heal" => effect.Target is "LowestHpAlly" or "AllAlive" or "Self", "Guard" => effect.Target is "FrontAlly" or "Self",
            "Cleanse" => effect.Target is "FirstDebuffedAlly" or "Self", "ApplyStatus" => effect.Target is "Monster" or "Self" or "FrontAlly",
            "CooldownReduction" => effect.Target == "Self", _ => false };
        if (!validTarget || effect.Power < 0 || effect.AttackPowerPercent is < 0 or > 1000 || effect.HealMaxHpPercent is < 0 or > 100) return false;
        if (effect.Type == "Damage" && effect.Power == 0 && effect.AttackPowerPercent == 0) return false;
        if (effect.Type == "Heal" && effect.Power == 0 && effect.HealMaxHpPercent == 0) return false;
        if (effect.Type == "Guard" && effect.Power is <= 0 or > 100) return false;
        if (effect.Type == "CooldownReduction" && effect.Power is <= 0 or > 10) return false;
        return effect.Type != "ApplyStatus" || !string.IsNullOrWhiteSpace(effect.StatusCode) && effect.DurationRounds > 0;
    }
}
