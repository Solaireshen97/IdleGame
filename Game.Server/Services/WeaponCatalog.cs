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
    private readonly IReadOnlyList<WeaponSkillGrowthSegmentOptions> _skillGrowth;
    private readonly IReadOnlyList<int> _dropQualityWeights;
    public int StartingAccountGold { get; }

    public WeaponCatalog(IOptions<WeaponOptions> options)
    {
        StartingAccountGold = options.Value.StartingAccountGold;
        if (StartingAccountGold < 0) throw new InvalidOperationException("Starting gold cannot be negative.");
        var enhancementCosts = options.Value.EnhancementFragmentCosts.Count == 0
            ? new List<int> { 2, 4, 8 }
            : options.Value.EnhancementFragmentCosts;
        if (enhancementCosts.Count != WeaponRules.MaxEnhancementPerSkill ||
            enhancementCosts.Any(cost => cost <= 0))
            throw new InvalidOperationException("Weapon enhancement costs must define three positive steps.");
        _enhancementFragmentCosts = enhancementCosts.ToList();
        var qualityOptions = options.Value.DropQualityWeights;
        var dropQualityWeights = new[]
        {
            qualityOptions.Common, qualityOptions.Uncommon, qualityOptions.Rare, qualityOptions.Epic
        };
        if (dropQualityWeights.Length != WeaponRules.MaxQualityBonusLevels + 1 ||
            dropQualityWeights.Any(weight => weight < 0) ||
            dropQualityWeights.Sum(weight => (long)weight) <= 0)
            throw new InvalidOperationException("Weapon drop quality weights must define four non-negative values with a positive total.");
        _dropQualityWeights = dropQualityWeights.ToList();
        _skillGrowth = options.Value.SkillGrowth.ToList();
        if (_skillGrowth.Count > 0 && (_skillGrowth.Any(segment => segment.MultiplierPercent <= 0) ||
            _skillGrowth[^1].MaximumLevel is not null ||
            _skillGrowth.Take(_skillGrowth.Count - 1).Any(segment => segment.MaximumLevel is null or <= 0) ||
            _skillGrowth.Take(_skillGrowth.Count - 1).Select(segment => segment.MaximumLevel!.Value)
                .Zip(_skillGrowth.Skip(1).Select(segment => segment.MaximumLevel ?? int.MaxValue), (left, right) => left < right)
                .Any(valid => !valid)))
            throw new InvalidOperationException("Invalid weapon skill growth curve.");
        foreach (var rule in options.Value.EffectRules)
        {
            if (!Enum.IsDefined(rule.EffectType) || rule.PercentPerLevel <= 0 || rule.MaximumPercent <= 0 ||
                IsProbability(rule.EffectType) && rule.MaximumPercent > 100 || !_effectRules.TryAdd(rule.EffectType, rule))
                throw new InvalidOperationException($"Invalid weapon effect rule: {rule.EffectType}");
        }
        // Legacy single-effect definitions remain readable. They must agree on the
        // rate for a shared effect; aliases cannot create separate diminishing curves.
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
                item.Skills.Any(skill => skill.Level is < 1 or > WeaponRules.MaxSkillLevel || !_skills.ContainsKey(skill.Code)) ||
                item.Skills.Select(skill => skill.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != item.Skills.Count ||
                !_items.TryAdd(item.Code, item))
                throw new InvalidOperationException($"Invalid weapon configuration: {item.Code}");
        }
        foreach (var (profession, codes) in options.Value.StarterPacks)
        {
            if (string.IsNullOrWhiteSpace(profession) || codes.Count == 0 ||
                codes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != codes.Count ||
                codes.Any(code => !_items.ContainsKey(code)) || !_starterPacks.TryAdd(profession, codes))
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
        return codes.Select((code, index) =>
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
                Skills = item.Skills.Select((skill, skillIndex) => new CharacterWeaponSkill
                {
                    SlotIndex = skillIndex + 1,
                    SkillCode = skill.Code,
                    Level = skill.Level,
                    BaseLevel = skill.Level,
                    SpentFragments = 0
                }).ToList(),
                EquippedSlotIndex = index == 0 ? 1 : null
            };
        }).ToList();
    }

    public WeaponSkillDefinitionOptions? FindSkill(string? code) =>
        code is not null && _skills.TryGetValue(code, out var skill) ? skill : null;

    public WeaponTemplateOptions? FindItem(string? code) =>
        code is not null && _items.TryGetValue(_replacements.GetValueOrDefault(code) ?? code, out var item) ? item : null;

    public bool NeedsTemplateUpdate(CharacterWeapon weapon) => FindItem(weapon.WeaponCode) is { } item &&
        (weapon.TemplateRevision < item.Revision || !string.Equals(weapon.WeaponCode, item.Code, StringComparison.OrdinalIgnoreCase));

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
        weapon.Skills = item.Skills.Select((grant, index) =>
        {
            var old = existing.GetValueOrDefault(index + 1);
            var quality = old?.QualityBonusLevel ?? 0;
            var enhancement = old?.EnhancementLevel ?? 0;
            if (grant.Level + quality + enhancement > WeaponRules.MaxSkillLevel)
                throw new InvalidOperationException($"Weapon revision exceeds skill level limit: {item.Code}");
            return new CharacterWeaponSkill { SlotIndex = index + 1, SkillCode = grant.Code,
                BaseLevel = grant.Level, QualityBonusLevel = quality, EnhancementLevel = enhancement,
                Level = grant.Level + quality + enhancement, SpentFragments = old is null ? 0 : InvestedFragments(old) };
        }).ToList();
        weapon.Version++;
    }

    public CharacterWeapon MaterializeReward(WeaponRewardSnapshot snapshot, int characterId)
    {
        var weapon = snapshot.ToCharacterWeapon(characterId);
        if (NeedsTemplateUpdate(weapon)) ApplyTemplate(weapon);
        return weapon;
    }

    public int MaxFragmentTier => _items.Values.Select(item => WeaponRules.FragmentTier(item.ItemLevel)).DefaultIfEmpty(1).Max();

    public int EnhancementCost(int completedEnhancements) =>
        completedEnhancements is >= 0 and < WeaponRules.MaxEnhancementPerSkill
            ? _enhancementFragmentCosts[completedEnhancements]
            : throw new ArgumentOutOfRangeException(nameof(completedEnhancements));

    public int InvestedFragments(CharacterWeaponSkill skill) => skill.SpentFragments ?? skill.EnhancementLevel switch
    {
        0 => 0, 1 => 2, 2 => 6, 3 => 14,
        _ => throw new InvalidOperationException("Invalid historical enhancement rank.")
    };

    public static bool CanSell(CharacterWeapon weapon) => weapon.Origin != WeaponOrigin.Starter;

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
        EffectsFor(definition).Select(effect => $"{WeaponEffectLabels.Name(effect.EffectType)}有效等级 {(level * effect.LevelWeight):0.##}"));

    // Compatibility value for single-effect callers. Composite effects are exposed individually.
    public decimal CalculateSkillPercent(WeaponSkillDefinitionOptions definition, int level) =>
        EffectsFor(definition).Count == 1
            ? CalculateEffectPercent(EffectsFor(definition)[0].EffectType, level * EffectsFor(definition)[0].LevelWeight) : 0;

    public decimal CalculateEffectPercent(WeaponSkillEffectType effect, decimal level)
    {
        if (level <= 0) return 0;
        var rule = _effectRules[effect];
        if (_skillGrowth.Count == 0) return Math.Min(rule.MaximumPercent, level * rule.PercentPerLevel);
        var total = 0m;
        var consumed = 0m;
        foreach (var segment in _skillGrowth)
        {
            var segmentLevels = segment.MaximumLevel is int maximum
                ? Math.Min(level, maximum) - consumed
                : level - consumed;
            if (segmentLevels > 0)
                total += segmentLevels * rule.PercentPerLevel * segment.MultiplierPercent / 100m;
            if (segment.MaximumLevel is int end) consumed = end;
            if (consumed >= level || segment.MaximumLevel is null) break;
        }
        return Math.Min(rule.MaximumPercent, total);
    }

    public WeaponRewardSnapshot CreateRewardSnapshot(string code)
    {
        var item = FindItem(code) ?? throw new InvalidOperationException($"Unknown weapon reward: {code}");
        return new WeaponRewardSnapshot(item.Code, item.Name, item.Element, item.Attack, item.MaxHp,
            item.ItemLevel, item.SellGold, item.DismantleFragments,
            item.Skills.Select(skill => new WeaponRewardSkillSnapshot(skill.Code, skill.Level)).ToList(), item.Revision);
    }

    public WeaponRewardSnapshot CreateDropSnapshot(string code, Random? random = null)
    {
        var snapshot = CreateRewardSnapshot(code);
        if (snapshot.Skills.Count == 0) return snapshot;

        random ??= Random.Shared;
        var targetBonusLevels = RollQualityBonusLevels(random);
        var allocated = new int[snapshot.Skills.Count];
        for (var point = 0; point < targetBonusLevels; point++)
        {
            var candidates = Enumerable.Range(0, snapshot.Skills.Count)
                .Where(index => snapshot.Skills[index].Level + allocated[index] +
                    WeaponRules.MaxEnhancementPerSkill < WeaponRules.MaxSkillLevel)
                .ToList();
            if (candidates.Count == 0) break;
            allocated[candidates[random.Next(candidates.Count)]]++;
        }

        return snapshot with
        {
            Skills = snapshot.Skills.Select((skill, index) =>
                skill with { QualityBonusLevel = allocated[index] }).ToList()
        };
    }

    private int RollQualityBonusLevels(Random random)
    {
        var totalWeight = _dropQualityWeights.Sum(weight => (long)weight);
        var roll = random.NextInt64(totalWeight);
        for (var index = 0; index < _dropQualityWeights.Count; index++)
        {
            if (roll < _dropQualityWeights[index]) return index;
            roll -= _dropQualityWeights[index];
        }
        return 0;
    }

    public WeaponSkillBonuses CalculateBonuses(IEnumerable<CharacterWeapon> weapons)
    {
        var equipped = weapons.Where(weapon => weapon.EquippedSlotIndex.HasValue).ToList();
        var main = equipped.SingleOrDefault(weapon => weapon.EquippedSlotIndex == WeaponRules.MainSlotIndex);
        if (main is null) return new WeaponSkillBonuses(0, 0, 0, [], []);

        var active = equipped.Where(weapon => weapon.Element == main.Element)
            .SelectMany(weapon => weapon.Skills)
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
                .Select(effect => (effect.EffectType, Level: skill.Level * effect.LevelWeight)))
            .GroupBy(effect => effect.EffectType)
            .Select(group => new WeaponEffectBonus(group.Key, group.Sum(effect => effect.Level),
                CalculateEffectPercent(group.Key, group.Sum(effect => effect.Level))))
            .OrderBy(effect => effect.EffectType).ToList();
        decimal Percent(WeaponSkillEffectType effect) => effects.SingleOrDefault(item => item.EffectType == effect)?.TotalPercent ?? 0;
        return new WeaponSkillBonuses(Percent(WeaponSkillEffectType.AttackPercent),
            Percent(WeaponSkillEffectType.MaxHpPercent), Percent(WeaponSkillEffectType.CriticalChancePercent), active, effects);
    }

    public WeaponSkillBonuses ApplyBonuses(Character character, IEnumerable<CharacterWeapon> weapons)
    {
        var bonuses = CalculateBonuses(weapons);
        character.WeaponAttackBonusPercent = bonuses.AttackPercent;
        character.WeaponHealthBonusPercent = bonuses.HealthPercent;
        character.WeaponCriticalChancePercent = bonuses.CriticalChancePercent;
        character.WeaponStaminaPercent = bonuses.Percent(WeaponSkillEffectType.StaminaPercent);
        character.WeaponEnmityPercent = bonuses.Percent(WeaponSkillEffectType.EnmityPercent);
        character.WeaponDoubleAttackChancePercent = bonuses.Percent(WeaponSkillEffectType.DoubleAttackChancePercent);
        character.WeaponNormalEchoPercent = bonuses.Percent(WeaponSkillEffectType.NormalEchoPercent);
        character.WeaponSkillDamagePercent = bonuses.Percent(WeaponSkillEffectType.SkillDamagePercent);
        return bonuses;
    }
}

public sealed record ActiveWeaponSkill(string Code, string Name, int Level, decimal TotalPercent, string Description);

public sealed record WeaponEffectBonus(WeaponSkillEffectType EffectType, decimal EffectiveLevel, decimal TotalPercent);

public sealed record WeaponSkillBonuses(decimal AttackPercent, decimal HealthPercent,
    decimal CriticalChancePercent, IReadOnlyList<ActiveWeaponSkill> ActiveSkills, IReadOnlyList<WeaponEffectBonus> Effects)
{
    public decimal Percent(WeaponSkillEffectType effect) => Effects.SingleOrDefault(item => item.EffectType == effect)?.TotalPercent ?? 0;
}
