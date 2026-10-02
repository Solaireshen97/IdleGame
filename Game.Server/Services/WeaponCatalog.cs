using Game.Server.Configuration;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class WeaponCatalog
{
    private readonly Dictionary<string, WeaponTemplateOptions> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WeaponSkillDefinitionOptions> _skills = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<WeaponSkillEffectType, WeaponEffectRuleOptions> _effectRules = new();
    private readonly Dictionary<string, List<string>> _starterPacks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _replacements = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<int> _enhancementFragmentCosts;
    private readonly int _starterEquippedSlotCount;
    public int StartingCharacterGold { get; }

    public WeaponCatalog(IOptions<WeaponOptions> options)
    {
        StartingCharacterGold = options.Value.StartingCharacterGold;
        if (StartingCharacterGold < 0) throw new InvalidOperationException("Starting gold cannot be negative.");
        _starterEquippedSlotCount = options.Value.StarterEquippedSlotCount;
        if (_starterEquippedSlotCount is < 1 or > WeaponRules.SlotCount)
            throw new InvalidOperationException("Starter equipped slot count is invalid.");
        var enhancementCosts = options.Value.EnhancementFragmentCosts.Count == 0
            ? new List<int> { 2, 4, 8, 8, 8, 8, 8, 8, 8 }
            : options.Value.EnhancementFragmentCosts;
        if (enhancementCosts.Count != WeaponRules.MaxEnhancementWithQuality ||
            enhancementCosts.Any(cost => cost <= 0))
            throw new InvalidOperationException("Weapon enhancement costs must define nine positive steps.");
        _enhancementFragmentCosts = enhancementCosts.ToList();
        foreach (var rule in options.Value.EffectRules)
        {
            if (!Enum.IsDefined(rule.EffectType) || rule.PercentPerLevel <= 0 || rule.MaximumPercent <= 0 ||
                IsProbability(rule.EffectType) && rule.MaximumPercent > 100 || !_effectRules.TryAdd(rule.EffectType, rule))
                throw new InvalidOperationException($"Invalid weapon effect rule: {rule.EffectType}");
        }
        // Legacy single-effect definitions remain readable. They must agree on the
        // rate for a shared effect; aliases cannot create separate effect rules.
        foreach (var group in options.Value.Skills.Where(skill => skill.Effects.Count == 0).GroupBy(skill => skill.EffectType))
        {
            if (_effectRules.ContainsKey(group.Key)) continue;
            if (group.Any(skill => skill.PercentPerLevel <= 0) || group.Select(skill => skill.PercentPerLevel).Distinct().Count() != 1)
                throw new InvalidOperationException($"Inconsistent legacy weapon effect: {group.Key}");
            _effectRules.Add(group.Key, new WeaponEffectRuleOptions { EffectType = group.Key,
                PercentPerLevel = group.First().PercentPerLevel, MaximumPercent = IsProbability(group.Key) ? 100 : decimal.MaxValue });
        }
        foreach (var skill in options.Value.Skills)
        {
            if (string.IsNullOrWhiteSpace(skill.Code) || string.IsNullOrWhiteSpace(skill.Name) ||
                EffectsFor(skill).Any(effect => !Enum.IsDefined(effect.EffectType) || effect.LevelWeight <= 0 ||
                    effect.PercentPerLevel is <= 0 ||
                    !_effectRules.ContainsKey(effect.EffectType)) ||
                EffectsFor(skill).Select(effect => effect.EffectType).Distinct().Count() != EffectsFor(skill).Count ||
                !_skills.TryAdd(skill.Code, skill))
                throw new InvalidOperationException($"Invalid weapon skill configuration: {skill.Code}");
        }
        foreach (var item in options.Value.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Code) || string.IsNullOrWhiteSpace(item.Name) ||
                !Enum.IsDefined(item.Element) || item.Attack < 0 || item.MaxHp <= 0 || item.ItemLevel <= 0 ||
                item.SellGold < 0 || item.DismantleFragments <= 0 || item.Revision < 0 ||
                item.Skills.Count > WeaponRules.MaxSkillsPerWeapon ||
                item.Skills.Any(skill => skill.Level is < 1 or > WeaponRules.MaxSkillLevel ||
                    skill.UnlockQualityRank is < 0 or > WeaponRules.MaxQualityBonusLevels || !_skills.ContainsKey(skill.Code)) ||
                item.Skills.Select(skill => skill.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != item.Skills.Count ||
                !_items.TryAdd(item.Code, item))
                throw new InvalidOperationException($"Invalid weapon configuration: {item.Code}");
        }
        foreach (var (profession, codes) in options.Value.StarterPacks)
        {
            if (string.IsNullOrWhiteSpace(profession) || codes.Count == 0 ||
                codes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != codes.Count ||
                codes.Any(code => !_items.ContainsKey(code)) ||
                (_starterEquippedSlotCount > 1 && codes.Count != 1) ||
                !_starterPacks.TryAdd(profession, codes))
                throw new InvalidOperationException($"Invalid starter weapons for: {profession}");
        }
        if (_items.Count == 0 || _starterPacks.Count == 0)
            throw new InvalidOperationException("Weapons and starter packs must be configured.");
        foreach (var (oldCode, newCode) in options.Value.Replacements)
        {
            if (string.IsNullOrWhiteSpace(oldCode) || _items.ContainsKey(oldCode) || !_items.ContainsKey(newCode) ||
                !_replacements.TryAdd(oldCode, newCode))
                throw new InvalidOperationException($"Invalid retired weapon mapping: {oldCode}");
        }
    }

    public IReadOnlyList<CharacterWeapon> CreateStarterWeapons(int characterId, string professionCode)
    {
        var fallbackCode = professionCode switch
        {
            "swordsman" => "knight", "acolyte" => "cleric",
            "knight" => "swordsman", "cleric" => "acolyte", _ => professionCode
        };
        if (!_starterPacks.TryGetValue(professionCode, out var codes) && !_starterPacks.TryGetValue(fallbackCode, out codes))
            throw new InvalidOperationException($"Missing starter weapons for: {professionCode}");
        var starterCodes = _starterEquippedSlotCount > 1
            ? Enumerable.Repeat(codes[0], _starterEquippedSlotCount)
            : codes;
        return starterCodes.Select((code, index) =>
        {
            var item = _items[code];
            return new CharacterWeapon
            {
                CharacterId = characterId,
                WeaponCode = item.Code,
                TemplateRevision = item.Revision,
                Origin = WeaponOrigin.Starter,
                Name = item.Name,
                Element = item.Element,
                Attack = item.Attack,
                MaxHp = item.MaxHp,
                ItemLevel = item.ItemLevel,
                SellGold = item.SellGold,
                DismantleFragments = item.DismantleFragments,
                IsLocked = index == 0,
                Skills = item.Skills.Select((skill, skillIndex) => (skill, skillIndex))
                    .Where(entry => entry.skill.UnlockQualityRank == 0)
                    .Select(entry => new CharacterWeaponSkill
                {
                    SlotIndex = entry.skillIndex + 1,
                    SkillCode = entry.skill.Code,
                    Level = entry.skill.Level,
                    BaseLevel = entry.skill.Level,
                    SpentFragments = 0
                }).ToList(),
                EquippedSlotIndex = _starterEquippedSlotCount > 1 ? index + 1 : index == 0 ? 1 : null
            };
        }).ToList();
    }

    public WeaponSkillDefinitionOptions? FindSkill(string? code) =>
        code is not null && _skills.TryGetValue(code, out var skill) ? skill : null;

    public WeaponTemplateOptions? FindItem(string? code) =>
        code is not null && _items.TryGetValue(_replacements.GetValueOrDefault(code) ?? code, out var item) ? item : null;

    public bool NeedsTemplateUpdate(CharacterWeapon weapon) => FindItem(weapon.WeaponCode) is { } item &&
        (weapon.TemplateRevision < item.Revision || !string.Equals(weapon.WeaponCode, item.Code, StringComparison.OrdinalIgnoreCase));

    public void SynchronizeName(CharacterWeapon weapon)
    {
        var item = FindItem(weapon.WeaponCode);
        if (item is null || weapon.Name == item.Name) return;
        // Renaming must not rebase stats, replace skill rows, or reset existing investments.
        weapon.Name = item.Name;
        weapon.Version++;
    }

    public void ApplyTemplate(CharacterWeapon weapon)
    {
        var item = FindItem(weapon.WeaponCode) ?? throw new InvalidOperationException($"Unknown weapon: {weapon.WeaponCode}");
        if (weapon.Skills.Count > item.Skills.Count)
            throw new InvalidOperationException($"Weapon revision must not discard skill investment: {weapon.WeaponCode}");
        var existing = weapon.Skills.ToDictionary(skill => skill.SlotIndex);
        weapon.WeaponCode = item.Code;
        weapon.TemplateRevision = item.Revision;
        weapon.Name = item.Name;
        weapon.Element = item.Element;
        weapon.Attack = item.Attack;
        weapon.MaxHp = item.MaxHp;
        weapon.ItemLevel = item.ItemLevel;
        weapon.SellGold = item.SellGold;
        weapon.DismantleFragments = item.DismantleFragments;
        weapon.Skills = item.Skills.Select((grant, index) => (grant, index))
            .Where(entry => entry.grant.UnlockQualityRank <= weapon.QualityRank ||
                existing.ContainsKey(entry.index + 1))
            .Select(entry =>
        {
            var (grant, index) = entry;
            var old = existing.GetValueOrDefault(index + 1);
            var enhancement = old?.EnhancementLevel ?? 0;
            if (grant.Level + enhancement > WeaponRules.MaxSkillLevel)
                throw new InvalidOperationException($"Weapon revision exceeds skill level limit: {item.Code}");
            return new CharacterWeaponSkill { SlotIndex = index + 1, SkillCode = grant.Code,
                BaseLevel = grant.Level, EnhancementLevel = enhancement,
                Level = grant.Level + enhancement, SpentFragments = old is null ? 0 : InvestedFragments(old) };
        }).ToList();
        weapon.Version++;
    }

    public void UnlockSkillsForQuality(CharacterWeapon weapon)
    {
        var item = FindItem(weapon.WeaponCode);
        if (item is null) return;
        foreach (var (grant, index) in item.Skills.Select((grant, index) => (grant, index)))
        {
            if (grant.UnlockQualityRank == 0 || grant.UnlockQualityRank > weapon.QualityRank ||
                weapon.Skills.Any(skill => skill.SlotIndex == index + 1)) continue;
            weapon.Skills.Add(new CharacterWeaponSkill
            {
                SlotIndex = index + 1, SkillCode = grant.Code, BaseLevel = grant.Level,
                Level = grant.Level, SpentFragments = 0
            });
        }
    }

    public CharacterWeapon MaterializeReward(WeaponRewardSnapshot snapshot, int characterId)
    {
        var weapon = snapshot.ToCharacterWeapon(characterId);
        if (NeedsTemplateUpdate(weapon)) ApplyTemplate(weapon);
        else SynchronizeName(weapon);
        return weapon;
    }

    public int MaxFragmentTier => _items.Values.Select(item => WeaponRules.FragmentTier(item.ItemLevel)).DefaultIfEmpty(1).Max();
    public IEnumerable<int> FragmentTiers => _items.Values.Select(item => WeaponRules.FragmentTier(item.ItemLevel)).Append(1).Distinct();

    public int EnhancementCost(int completedEnhancements) =>
        completedEnhancements is >= 0 and < WeaponRules.MaxEnhancementWithQuality
            ? _enhancementFragmentCosts[completedEnhancements]
            : throw new ArgumentOutOfRangeException(nameof(completedEnhancements));

    // Persisted spending is authoritative. Null belongs to pre-tracking skills, so retain
    // their historical 2/4/8/16/32/64 estimate instead of repricing them on each balance change.
    public int InvestedFragments(CharacterWeaponSkill skill) => skill.SpentFragments ?? skill.EnhancementLevel switch
    {
        0 => 0, 1 => 2, 2 => 6, 3 => 14, 4 => 30, 5 => 62, 6 => 126,
        7 => 134, 8 => 142, 9 => 150,
        _ => throw new InvalidOperationException("Invalid historical enhancement rank.")
    };

    public static int BaseDismantleReturn(CharacterWeapon weapon) => weapon.Origin is WeaponOrigin.Starter or WeaponOrigin.Shop
        ? 0 : weapon.DismantleFragments;

    public int DismantleReturn(CharacterWeapon weapon) => checked(BaseDismantleReturn(weapon) +
        weapon.Skills.Sum(InvestedFragments) / 2);

    public bool CanDismantle(CharacterWeapon weapon) => DismantleReturn(weapon) > 0;

    private static bool IsProbability(WeaponSkillEffectType effect) => effect is
        WeaponSkillEffectType.CriticalChancePercent or WeaponSkillEffectType.DoubleAttackChancePercent;

    public static IReadOnlyList<WeaponSkillEffectOptions> EffectsFor(WeaponSkillDefinitionOptions definition) =>
        definition.Effects.Count > 0 ? definition.Effects : [new() { EffectType = definition.EffectType }];

    public string DescribeSkill(WeaponSkillDefinitionOptions definition, int level) => string.Join(" · ",
        EffectsFor(definition).Select(effect =>
        {
            var value = SkillEffectPercent(effect, level);
            return effect.EffectType switch
            {
                WeaponSkillEffectType.RampAttackPercent => $"精进每回合 +{value:0.##}%（第10回合封顶）",
                WeaponSkillEffectType.ElementAdvantagePercent => $"克制优势 +{value:0.##}%",
                WeaponSkillEffectType.StaminaPercent or WeaponSkillEffectType.EnmityPercent or
                    WeaponSkillEffectType.LowHpDamageReductionPercent =>
                    $"{WeaponEffectLabels.Name(effect.EffectType)}最高 +{value:0.##}%",
                _ => $"{WeaponEffectLabels.Name(effect.EffectType)} +{value:0.##}%"
            };
        }));

    // Compatibility value for single-effect callers. Composite effects are exposed individually.
    public decimal CalculateSkillPercent(WeaponSkillDefinitionOptions definition, int level) =>
        EffectsFor(definition).Count == 1
            ? Math.Min(_effectRules[EffectsFor(definition)[0].EffectType].MaximumPercent,
                SkillEffectPercent(EffectsFor(definition)[0], level)) : 0;

    private decimal SkillEffectPercent(WeaponSkillEffectOptions effect, decimal level) =>
        level * effect.LevelWeight * (effect.PercentPerLevel ?? _effectRules[effect.EffectType].PercentPerLevel);

    public decimal CalculateEffectPercent(WeaponSkillEffectType effect, decimal level)
    {
        if (level <= 0) return 0;
        var rule = _effectRules[effect];
        return Math.Min(rule.MaximumPercent, level * rule.PercentPerLevel);
    }

    public WeaponRewardSnapshot CreateRewardSnapshot(string code)
    {
        var item = FindItem(code) ?? throw new InvalidOperationException($"Unknown weapon reward: {code}");
        return new WeaponRewardSnapshot(item.Code, item.Name, item.Element, item.Attack, item.MaxHp,
            item.ItemLevel, item.SellGold, item.DismantleFragments,
            item.Skills.Where(skill => skill.UnlockQualityRank == 0)
                .Select(skill => new WeaponRewardSkillSnapshot(skill.Code, skill.Level)).ToList(), item.Revision);
    }

    public WeaponSkillBonuses CalculateBonuses(IEnumerable<CharacterWeapon> weapons,
        IReadOnlyDictionary<string, int>? temporarySkillLevels = null)
    {
        var equipped = weapons.Where(weapon => weapon.EquippedSlotIndex.HasValue).ToList();
        var main = equipped.SingleOrDefault(weapon => weapon.EquippedSlotIndex == WeaponRules.MainSlotIndex);
        if (main is null && (temporarySkillLevels is null || temporarySkillLevels.Count == 0))
            return new WeaponSkillBonuses(0, 0, 0, [], []);

        var skillLevels = equipped.Where(weapon => main is not null && weapon.Element == main.Element)
            .SelectMany(weapon => weapon.Skills)
            .Select(skill => (skill.SkillCode, skill.Level));
        if (temporarySkillLevels is not null)
            skillLevels = skillLevels.Concat(temporarySkillLevels.Select(skill =>
                (SkillCode: skill.Key, Level: skill.Value)));
        var active = skillLevels
            .GroupBy(skill => skill.SkillCode, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var definition = FindSkill(group.Key);
                var level = group.Sum(skill => skill.Level);
                return definition is null ? null : new ActiveWeaponSkill(
                    definition.Code, definition.Name, level, CalculateSkillPercent(definition, level),
                    DescribeSkill(definition, level));
            })
            .OfType<ActiveWeaponSkill>()
            .OrderBy(skill => skill.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var effects = active.SelectMany(skill => EffectsFor(FindSkill(skill.Code)!)
                .Select(effect => (effect.EffectType, Level: skill.Level * effect.LevelWeight,
                    Percent: SkillEffectPercent(effect, skill.Level))))
            .GroupBy(effect => effect.EffectType)
            .Select(group => new WeaponEffectBonus(group.Key, group.Sum(effect => effect.Level),
                Math.Min(_effectRules[group.Key].MaximumPercent, group.Sum(effect => effect.Percent))))
            .OrderBy(effect => effect.EffectType).ToList();
        var directReduction = effects.SingleOrDefault(effect =>
            effect.EffectType == WeaponSkillEffectType.DirectDamageReductionPercent)?.TotalPercent ?? 0;
        var lowHpReductionIndex = effects.FindIndex(effect =>
            effect.EffectType == WeaponSkillEffectType.LowHpDamageReductionPercent);
        if (lowHpReductionIndex >= 0)
            effects[lowHpReductionIndex] = effects[lowHpReductionIndex] with
            {
                TotalPercent = Math.Min(effects[lowHpReductionIndex].TotalPercent,
                    Math.Max(0, 50m - directReduction))
            };
        decimal Percent(WeaponSkillEffectType effect) => effects.SingleOrDefault(item => item.EffectType == effect)?.TotalPercent ?? 0;
        return new WeaponSkillBonuses(Percent(WeaponSkillEffectType.AttackPercent),
            Percent(WeaponSkillEffectType.MaxHpPercent), Percent(WeaponSkillEffectType.CriticalChancePercent), active, effects);
    }

    public WeaponSkillBonuses ApplyBonuses(Character character, IEnumerable<CharacterWeapon> weapons)
    {
        var bonuses = CalculateBonuses(weapons);
        CharacterEquipmentStats.ApplyBonusesTo(character, bonuses);
        return bonuses;
    }

    public CharacterEquipmentStats RecalculateEquipmentStats(Character character, IEnumerable<CharacterWeapon> weapons)
    {
        var stats = CharacterEquipmentStats.Calculate(weapons, this);
        stats.ApplyTo(character);
        return stats;
    }
}

public sealed record ActiveWeaponSkill(string Code, string Name, int Level, decimal TotalPercent, string Description);

public sealed record WeaponEffectBonus(WeaponSkillEffectType EffectType, decimal EffectiveLevel, decimal TotalPercent);

public sealed record WeaponSkillBonuses(decimal AttackPercent, decimal HealthPercent,
    decimal CriticalChancePercent, IReadOnlyList<ActiveWeaponSkill> ActiveSkills, IReadOnlyList<WeaponEffectBonus> Effects)
{
    public decimal Percent(WeaponSkillEffectType effect) => Effects.SingleOrDefault(item => item.EffectType == effect)?.TotalPercent ?? 0;
}
