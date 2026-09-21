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
    private readonly Dictionary<string, List<string>> _starterPacks = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<int> _enhancementFragmentCosts;
    private readonly IReadOnlyList<WeaponSkillGrowthSegmentOptions> _skillGrowth;

    public WeaponCatalog(IOptions<WeaponOptions> options)
    {
        var enhancementCosts = options.Value.EnhancementFragmentCosts.Count == 0
            ? new List<int> { 2, 4, 8 }
            : options.Value.EnhancementFragmentCosts;
        if (enhancementCosts.Count != WeaponRules.MaxEnhancementPerSkill ||
            enhancementCosts.Any(cost => cost <= 0))
            throw new InvalidOperationException("Weapon enhancement costs must define three positive steps.");
        _enhancementFragmentCosts = enhancementCosts.ToList();
        _skillGrowth = options.Value.SkillGrowth.ToList();
        if (_skillGrowth.Count > 0 && (_skillGrowth.Any(segment => segment.MultiplierPercent <= 0) ||
            _skillGrowth[^1].MaximumLevel is not null ||
            _skillGrowth.Take(_skillGrowth.Count - 1).Any(segment => segment.MaximumLevel is null or <= 0) ||
            _skillGrowth.Take(_skillGrowth.Count - 1).Select(segment => segment.MaximumLevel!.Value)
                .Zip(_skillGrowth.Skip(1).Select(segment => segment.MaximumLevel ?? int.MaxValue), (left, right) => left < right)
                .Any(valid => !valid)))
            throw new InvalidOperationException("Invalid weapon skill growth curve.");
        foreach (var skill in options.Value.Skills)
        {
            if (string.IsNullOrWhiteSpace(skill.Code) || string.IsNullOrWhiteSpace(skill.Name) ||
                !Enum.IsDefined(skill.EffectType) || skill.PercentPerLevel <= 0 ||
                !_skills.TryAdd(skill.Code, skill))
                throw new InvalidOperationException($"Invalid weapon skill configuration: {skill.Code}");
        }
        foreach (var item in options.Value.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Code) || string.IsNullOrWhiteSpace(item.Name) ||
                !Enum.IsDefined(item.Element) || item.Attack < 0 || item.MaxHp <= 0 || item.ItemLevel <= 0 ||
                item.SellGold < 0 || item.DismantleFragments <= 0 ||
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
    }

    public IReadOnlyList<CharacterWeapon> CreateStarterWeapons(int characterId, string professionCode)
    {
        if (!_starterPacks.TryGetValue(professionCode, out var codes))
            throw new InvalidOperationException($"Missing starter weapons for: {professionCode}");
        return codes.Select((code, index) =>
        {
            var item = _items[code];
            return new CharacterWeapon
            {
                CharacterId = characterId,
                WeaponCode = item.Code,
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
                    BaseLevel = skill.Level
                }).ToList(),
                EquippedSlotIndex = index == 0 ? 1 : null
            };
        }).ToList();
    }

    public WeaponSkillDefinitionOptions? FindSkill(string? code) =>
        code is not null && _skills.TryGetValue(code, out var skill) ? skill : null;

    public WeaponTemplateOptions? FindItem(string? code) =>
        code is not null && _items.TryGetValue(code, out var item) ? item : null;

    public int MaxFragmentTier => _items.Values.Select(item => WeaponRules.FragmentTier(item.ItemLevel)).DefaultIfEmpty(1).Max();

    public int EnhancementCost(int completedEnhancements) =>
        completedEnhancements is >= 0 and < WeaponRules.MaxEnhancementPerSkill
            ? _enhancementFragmentCosts[completedEnhancements]
            : throw new ArgumentOutOfRangeException(nameof(completedEnhancements));

    public int InvestedFragments(CharacterWeaponSkill skill) =>
        Enumerable.Range(0, skill.EnhancementLevel).Sum(EnhancementCost);

    public int DismantleReturn(CharacterWeapon weapon) => checked(weapon.DismantleFragments +
        weapon.Skills.Sum(InvestedFragments) / 2);

    public decimal CalculateSkillPercent(WeaponSkillDefinitionOptions definition, int level)
    {
        if (level <= 0) return 0;
        if (_skillGrowth.Count == 0) return level * definition.PercentPerLevel;
        var total = 0m;
        var consumed = 0;
        foreach (var segment in _skillGrowth)
        {
            var segmentLevels = segment.MaximumLevel is int maximum
                ? Math.Min(level, maximum) - consumed
                : level - consumed;
            if (segmentLevels > 0)
                total += segmentLevels * definition.PercentPerLevel * segment.MultiplierPercent / 100m;
            if (segment.MaximumLevel is int end) consumed = end;
            if (consumed >= level || segment.MaximumLevel is null) break;
        }
        return total;
    }

    public WeaponRewardSnapshot CreateRewardSnapshot(string code)
    {
        var item = FindItem(code) ?? throw new InvalidOperationException($"Unknown weapon reward: {code}");
        return new WeaponRewardSnapshot(item.Code, item.Name, item.Element, item.Attack, item.MaxHp,
            item.ItemLevel, item.SellGold, item.DismantleFragments,
            item.Skills.Select(skill => new WeaponRewardSkillSnapshot(skill.Code, skill.Level)).ToList());
    }

    public WeaponSkillBonuses CalculateBonuses(IEnumerable<CharacterWeapon> weapons)
    {
        var equipped = weapons.Where(weapon => weapon.EquippedSlotIndex.HasValue).ToList();
        var main = equipped.SingleOrDefault(weapon => weapon.EquippedSlotIndex == WeaponRules.MainSlotIndex);
        if (main is null) return new WeaponSkillBonuses(0, 0, 0, []);

        var active = equipped.Where(weapon => weapon.Element == main.Element)
            .SelectMany(weapon => weapon.Skills)
            .GroupBy(skill => skill.SkillCode, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var definition = FindSkill(group.Key);
                var level = group.Sum(skill => skill.Level);
                return definition is null ? null : new ActiveWeaponSkill(
                    definition.Code, definition.Name, definition.EffectType,
                    level, CalculateSkillPercent(definition, level));
            })
            .OfType<ActiveWeaponSkill>()
            .OrderBy(skill => skill.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new WeaponSkillBonuses(
            active.Where(skill => skill.EffectType == WeaponSkillEffectType.AttackPercent).Sum(skill => skill.TotalPercent),
            active.Where(skill => skill.EffectType == WeaponSkillEffectType.MaxHpPercent).Sum(skill => skill.TotalPercent),
            Math.Min(100m, active.Where(skill => skill.EffectType == WeaponSkillEffectType.CriticalChancePercent).Sum(skill => skill.TotalPercent)),
            active);
    }

    public WeaponSkillBonuses ApplyBonuses(Character character, IEnumerable<CharacterWeapon> weapons)
    {
        var bonuses = CalculateBonuses(weapons);
        character.WeaponAttackBonusPercent = bonuses.AttackPercent;
        character.WeaponHealthBonusPercent = bonuses.HealthPercent;
        character.WeaponCriticalChancePercent = bonuses.CriticalChancePercent;
        return bonuses;
    }
}

public sealed record ActiveWeaponSkill(string Code, string Name, WeaponSkillEffectType EffectType,
    int Level, decimal TotalPercent);

public sealed record WeaponSkillBonuses(decimal AttackPercent, decimal HealthPercent,
    decimal CriticalChancePercent, IReadOnlyList<ActiveWeaponSkill> ActiveSkills);
