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

    public WeaponCatalog(IOptions<WeaponOptions> options)
    {
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
                !Enum.IsDefined(item.Element) || item.Attack < 0 || item.MaxHp <= 0 ||
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
                Skills = item.Skills.Select((skill, skillIndex) => new CharacterWeaponSkill
                {
                    SlotIndex = skillIndex + 1,
                    SkillCode = skill.Code,
                    Level = skill.Level
                }).ToList(),
                EquippedSlotIndex = index == 0 ? 1 : null
            };
        }).ToList();
    }

    public WeaponSkillDefinitionOptions? FindSkill(string? code) =>
        code is not null && _skills.TryGetValue(code, out var skill) ? skill : null;

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
                return definition is null ? null : new ActiveWeaponSkill(
                    definition.Code, definition.Name, definition.EffectType,
                    group.Sum(skill => skill.Level), definition.PercentPerLevel);
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
    int Level, decimal PercentPerLevel)
{
    public decimal TotalPercent => Level * PercentPerLevel;
}

public sealed record WeaponSkillBonuses(decimal AttackPercent, decimal HealthPercent,
    decimal CriticalChancePercent, IReadOnlyList<ActiveWeaponSkill> ActiveSkills);
