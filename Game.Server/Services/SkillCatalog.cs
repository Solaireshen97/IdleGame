using Game.Server.Configuration;
using Game.Shared;
using Game.Shared.Models;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class SkillCatalog
{
    private readonly Dictionary<string, ProfessionOptions> _professions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CombatSkillOptions> _skills = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CharacterSkillDefinition[]> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, decimal> _legacyTalentValues = new(StringComparer.OrdinalIgnoreCase);

    public SkillCatalog(IOptions<SkillOptions> options, MonsterCombatCatalog? monsterCombatCatalog = null)
    {
        foreach (var profession in options.Value.Professions.Select(Clone))
        {
            if (string.IsNullOrWhiteSpace(profession.Code) || string.IsNullOrWhiteSpace(profession.Name) ||
                profession.StartingSkills.Count > SkillRules.SlotCount || profession.RequiredLevel < 1 ||
                !_professions.TryAdd(profession.Code, profession))
                throw new InvalidOperationException($"Invalid profession configuration: {profession.Code}");
        }

        foreach (var skill in options.Value.Abilities.Select(Clone))
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
                AutoConditionFor(skill) is not ("Always" or "LowestHpBelowThreshold" or "AllyHasDebuff" or "MonsterHasBuff" or "InterruptibleIntent" or "PreferInterrupt") ||
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
                    !ValidVariant(skill, skill.Level2, monsterCombatCatalog) ||
                    !ValidVariant(skill, skill.Level3, monsterCombatCatalog) ||
                    !ValidVariant(ApplyVariant(skill, skill.Level3), skill.SharedVersion, monsterCombatCatalog))
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
            if (!string.IsNullOrWhiteSpace(node.Code)) _legacyTalentValues.TryAdd(node.Code, node.ValuePerRank);

        foreach (var skill in _skills.Values)
            _definitions.Add(skill.Code,
            [
                CharacterSkillDefinition.Compile(skill, 1, false),
                CharacterSkillDefinition.Compile(ApplyVariant(skill, skill.Level2), 2, false),
                CharacterSkillDefinition.Compile(ApplyVariant(skill, skill.Level3), 3, false),
                CharacterSkillDefinition.Compile(ApplyVariant(ApplyVariant(skill, skill.Level3), skill.SharedVersion), 3, true)
            ]);
    }

    public IReadOnlyCollection<ProfessionOptions> Professions => _professions.Values.Select(Clone).ToArray();
    public IReadOnlyList<ProfessionOptions> BaseProfessions => _professions.Values.Where(item => !item.IsPromotion).Select(Clone).ToList();
    public ProfessionOptions? FindProfession(string? code)
    {
        if (code is not null && _professions.TryGetValue(code, out var value)) return Clone(value);
        // Keeps isolated older test/config fixtures readable while production uses the formal base professions.
        if (string.Equals(code, "swordsman", StringComparison.OrdinalIgnoreCase) && _professions.TryGetValue("knight", out value)) return Clone(value);
        if (string.Equals(code, "acolyte", StringComparison.OrdinalIgnoreCase) && _professions.TryGetValue("cleric", out value)) return Clone(value);
        return null;
    }
    public ProfessionOptions? EffectiveProfession(Character character) => FindProfession(character.ProfessionCode);
    public CombatSkillOptions? FindSkill(string? code) => code is not null && _skills.TryGetValue(code, out var value) ? Clone(value) : null;
    public decimal? LegacyTalentValue(string code) => _legacyTalentValues.TryGetValue(code, out var value) ? value : null;
    public CharacterSkillDefinition? FindDefinition(string? code) =>
        code is not null && _definitions.TryGetValue(code, out var versions) ? versions[0] : null;

    // Information-only access to the compiled battle definitions; Resolve remains
    // the authority for learning and equipment eligibility.
    public IReadOnlyList<CharacterSkillDefinition> SkillLevelPreviews(string code, bool shared = false) =>
        _definitions.TryGetValue(code, out var versions)
            ? shared ? [versions[3]] : versions.Take(3).ToArray()
            : [];

    public bool IsLearned(Character character, string? skillCode, IReadOnlyDictionary<string, int> ranks,
        IReadOnlyDictionary<string, int>? professionLevels = null) =>
        Resolve(character, skillCode, professionLevels) is not null;

    public CharacterSkillDefinition? Resolve(Character character, string? skillCode,
        IReadOnlyDictionary<string, int>? professionLevels = null)
    {
        if (skillCode is null || !_definitions.TryGetValue(skillCode, out var versions)) return null;
        var skill = versions[0];
        var currentProfession = FindProfession(character.ProfessionCode)?.Code ?? character.ProfessionCode;
        if (string.Equals(skill.ProfessionCode, currentProfession, StringComparison.OrdinalIgnoreCase))
            return character.Level < skill.UnlockLevel ? null : versions[RankFor(skill, character.Level) - 1];
        var source = FindProfession(skill.ProfessionCode);
        if (character.Level < SkillRules.SharedSkillEquipLevel || source?.SharedSkillCode is null ||
            !string.Equals(source.SharedSkillCode, skill.Code, StringComparison.OrdinalIgnoreCase) ||
            professionLevels is null || !professionLevels.TryGetValue(source.Code, out var sourceLevel) ||
            sourceLevel < SkillRules.SharedSkillUnlockLevel) return null;
        return versions[3];
    }

    public IReadOnlyList<CharacterSkillDefinition> NativeSkills(Character character) =>
        SkillsAtLevel(FindProfession(character.ProfessionCode)?.Code ?? character.ProfessionCode, character.Level);

    public IReadOnlyList<CharacterSkillDefinition> SkillsAtLevel(string professionCode, int level) =>
        _definitions.Values.Where(versions => string.Equals(versions[0].ProfessionCode, professionCode, StringComparison.OrdinalIgnoreCase) &&
            level >= versions[0].UnlockLevel).Select(versions => versions[RankFor(versions[0], level) - 1]).ToList();

    public IReadOnlyList<CharacterSkillDefinition> AvailableSharedSkills(Character character, IReadOnlyDictionary<string, int> professionLevels) =>
        _professions.Values.Where(profession => !profession.IsPromotion && profession.SharedSkillCode is not null &&
            !string.Equals(profession.Code, character.ProfessionCode, StringComparison.OrdinalIgnoreCase) &&
            professionLevels.GetValueOrDefault(profession.Code) >= SkillRules.SharedSkillUnlockLevel)
            .Select(profession => _definitions[profession.SharedSkillCode!][3]).ToList();

    public CombatSkillOptions? ResolveSkillForLevel(Character character, string? skillCode,
        IReadOnlyDictionary<string, int>? professionLevels = null) =>
        Resolve(character, skillCode, professionLevels) is { } skill ? CompatibilityOptions(skill) : null;

    public IReadOnlyList<CombatSkillOptions> LearnedSkills(Character character, IReadOnlyDictionary<string, int> ranks,
        IReadOnlyDictionary<string, int>? professionLevels = null)
    {
        return NativeSkills(character).Select(CompatibilityOptions).ToList();
    }

    public IReadOnlyList<CombatSkillOptions> SkillsForProfessionAtLevel(string professionCode, int level) =>
        SkillsAtLevel(professionCode, level).Select(CompatibilityOptions).ToList();

    public IReadOnlyList<CombatSkillOptions> SharedSkills(Character character, IReadOnlyDictionary<string, int> professionLevels) =>
        AvailableSharedSkills(character, professionLevels).Select(CompatibilityOptions).ToList();

    private CombatSkillOptions CompatibilityOptions(CharacterSkillDefinition resolved)
    {
        var source = Clone(_skills[resolved.Code]);
        var skill = ApplyVariant(source, resolved.Level == 3 ? source.Level3 : resolved.Level == 2 ? source.Level2 : null);
        return resolved.IsShared ? ApplyVariant(skill, source.SharedVersion) : skill;
    }

    public static int RankFor(CombatSkillOptions skill, int level) => level >= skill.Level3UnlockLevel ? 3 :
        level >= skill.Level2UnlockLevel ? 2 : 1;

    public static int RankFor(CharacterSkillDefinition skill, int level) => skill.IsShared ? 3 :
        level >= skill.Level3UnlockLevel ? 3 : level >= skill.Level2UnlockLevel ? 2 : 1;

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

    private static bool ValidVariant(CombatSkillOptions skill, CombatSkillVariantOptions? variant, MonsterCombatCatalog? monsters)
    {
        if (variant is null) return true;
        var resolved = ApplyVariant(skill, variant);
        var effects = EffectsFor(resolved);
        var hasConditionalDamage = resolved.ConditionalDamageBonusPercent > 0;
        return !string.IsNullOrWhiteSpace(resolved.Name) && !string.IsNullOrWhiteSpace(resolved.Description) &&
            resolved.CooldownRounds >= 0 && resolved.InitialCooldownRounds >= 0 &&
            resolved.AttackPowerPercent is >= 0 and <= 1000 && resolved.HealMaxHpPercent is >= 0 and <= 100 &&
            resolved.ConditionalDamageBonusPercent is >= 0 and <= 200 &&
            resolved.TargetHpBelowPercent is null or >= 1 and <= 100 &&
            AutoConditionFor(resolved) is "Always" or "LowestHpBelowThreshold" or "AllyHasDebuff" or "MonsterHasBuff" or "InterruptibleIntent" or "PreferInterrupt" &&
            hasConditionalDamage == (resolved.RequiredTargetStatusCode is not null || resolved.TargetHpBelowPercent is not null) &&
            (!hasConditionalDamage || effects.Any(effect => effect.Type == "Damage")) &&
            (resolved.RequiredTargetStatusCode is null || monsters is null || monsters.FindStatus(resolved.RequiredTargetStatusCode) is not null) &&
            (variant.Effects is null || variant.Effects.Count > 0) && effects.Count > 0 && effects.All(effect => IsValidEffect(effect) &&
                (effect.Type != "ApplyStatus" || monsters is null || monsters.FindStatus(effect.StatusCode) is not null));
    }

    public static IReadOnlyList<BattleSkillEffect> EffectsFor(CharacterSkillDefinition skill) => skill.Effects;
    public static string AutoConditionFor(CharacterSkillDefinition skill) => skill.AutoCondition;
    public static string PrimaryEffectType(CharacterSkillDefinition skill) => skill.Effects[0].Type;
    public static int PrimaryPower(CharacterSkillDefinition skill) => skill.Effects[0].Power;

    private static ProfessionOptions Clone(ProfessionOptions source) => new()
    {
        Code = source.Code, Name = source.Name, Description = source.Description, StartingSkills = [.. source.StartingSkills],
        SharedSkillCode = source.SharedSkillCode, IsPromotion = source.IsPromotion,
        BaseProfessionCode = source.BaseProfessionCode, RequiredLevel = source.RequiredLevel
    };

    private static CombatSkillOptions Clone(CombatSkillOptions source)
    {
        var copy = ApplyVariant(source, new CombatSkillVariantOptions());
        copy.Effects = source.Effects.Select(Clone).ToList();
        copy.Level2 = Clone(source.Level2);
        copy.Level3 = Clone(source.Level3);
        copy.SharedVersion = Clone(source.SharedVersion);
        return copy;
    }

    private static CombatSkillVariantOptions? Clone(CombatSkillVariantOptions? source) => source is null ? null : new()
    {
        Name = source.Name, Description = source.Description, Power = source.Power,
        AttackPowerPercent = source.AttackPowerPercent, HealMaxHpPercent = source.HealMaxHpPercent,
        CooldownRounds = source.CooldownRounds, InitialCooldownRounds = source.InitialCooldownRounds,
        ConditionalDamageBonusPercent = source.ConditionalDamageBonusPercent,
        RequiredTargetStatusCode = source.RequiredTargetStatusCode, TargetHpBelowPercent = source.TargetHpBelowPercent,
        AutoCondition = source.AutoCondition, Effects = source.Effects?.Select(Clone).ToList()
    };

    private static CombatSkillEffectOptions Clone(CombatSkillEffectOptions source) => new()
    {
        Type = source.Type, Target = source.Target, Power = source.Power, AttackPowerPercent = source.AttackPowerPercent,
        HealMaxHpPercent = source.HealMaxHpPercent, StatusCode = source.StatusCode, DurationRounds = source.DurationRounds
    };

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
            "Heal" => effect.Target is "LowestHpAlly" or "LowestHpAllyFixed" or "AllAlive" or "Self", "Guard" => effect.Target is "FrontAlly" or "FrontAllyFixed" or "Self" or "AllAlive" or "AllOtherAlive",
            "Cleanse" => effect.Target is "FirstDebuffedAlly" or "Self", "ApplyStatus" => effect.Target is "Monster" or "Self" or "FrontAlly" or "AllAlive",
            "CooldownReduction" => effect.Target == "Self", _ => false };
        if (!validTarget || effect.Power < 0 || effect.AttackPowerPercent is < 0 or > 1000 || effect.HealMaxHpPercent is < 0 or > 100) return false;
        if (effect.Type == "Damage" && effect.Power == 0 && effect.AttackPowerPercent == 0) return false;
        if (effect.Type == "Heal" && effect.Power == 0 && effect.HealMaxHpPercent == 0) return false;
        if (effect.Type == "Guard" && effect.Power is <= 0 or > 100) return false;
        if (effect.Type == "CooldownReduction" && effect.Power is <= 0 or > 10) return false;
        return effect.Type != "ApplyStatus" || !string.IsNullOrWhiteSpace(effect.StatusCode) && effect.DurationRounds > 0;
    }
}
