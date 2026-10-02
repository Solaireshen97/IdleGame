using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Game.Shared;
using Game.Shared.Dtos.Formations;

namespace Game.Server.Services;

public static class CombatLoadoutCodec
{
    public static CombatLoadoutDefinition Deserialize(string json)
    {
        try
        {
            var value = JsonSerializer.Deserialize<CombatLoadoutDefinition>(json);
            if (value is null || value.SchemaVersion != 1 || value.Weapons is null || value.Skills is null || value.Consumables is null)
                throw new FormatException("UnsupportedLoadoutSnapshot");
            if (string.IsNullOrWhiteSpace(value.ProfessionCode) || value.Weapons.Any(s => s is null) || value.Skills.Any(s => s is null) || value.Consumables.Any(s => s is null) ||
                !ValidSlots(value.Weapons.Select(s => s.SlotIndex), WeaponRules.SlotCount) ||
                !ValidSlots(value.Skills.Select(s => s.SlotIndex), SkillRules.SlotCount) ||
                !ValidSlots(value.Consumables.Select(s => s.SlotIndex), ConsumableRules.TotalSlotCount))
                throw new FormatException("InvalidLoadoutSnapshot");
            return value;
        }
        catch (JsonException exception) { throw new FormatException("InvalidLoadoutSnapshot", exception); }
    }

    public static string Serialize(CombatLoadoutDefinition value) => JsonSerializer.Serialize(value);
    private static bool ValidSlots(IEnumerable<int> indexes, int count)
    {
        var values = indexes.ToArray();
        return values.Length <= count && values.All(i => i >= 1 && i <= count) && values.Distinct().Count() == values.Length;
    }
    public static CombatLoadoutDefinition Clone(CombatLoadoutDefinition value) => Deserialize(Serialize(value));

    // Fill absent slots so an omitted empty slot and an explicitly empty slot compare equally.
    public static string ChoiceHash(CombatLoadoutDefinition value) => Hash(JsonSerializer.Serialize(new
    {
        value.SchemaVersion, ProfessionCode = Code(value.ProfessionCode),
        Weapons = Enumerable.Range(1, WeaponRules.SlotCount).Select(i => value.Weapons.FirstOrDefault(s => s.SlotIndex == i)?.WeaponId),
        Skills = Enumerable.Range(1, SkillRules.SlotCount).Select(i => Code(value.Skills.FirstOrDefault(s => s.SlotIndex == i)?.SkillCode)),
        Consumables = Enumerable.Range(1, ConsumableRules.TotalSlotCount).Select(i => Code(value.Consumables.FirstOrDefault(s => s.SlotIndex == i)?.ItemCode)),
        value.SoulImprintId
    }));

    public static string ConfigurationHash(CombatLoadoutDefinition value) => Hash(JsonSerializer.Serialize(new
    {
        Structure = ChoiceHash(value),
        Skills = Enumerable.Range(1, SkillRules.SlotCount).Select(i =>
        {
            var s = value.Skills.FirstOrDefault(s => s.SlotIndex == i);
            return new { Auto = s?.SkillCode is not null && s.AutoUseEnabled,
                Condition = s?.SkillCode is not null ? SkillAutoRules.Normalize(s.AutoConditionOverride) : null,
                Threshold = s?.AutoHpThresholdPercent ?? SkillRules.DefaultAutoHpThresholdPercent };
        }),
        Consumables = Enumerable.Range(1, ConsumableRules.TotalSlotCount).Select(i =>
        {
            var s = value.Consumables.FirstOrDefault(s => s.SlotIndex == i);
            return new { Auto = i != ConsumableRules.OperationPotionSlotIndex && s?.ItemCode is not null && s.AutoUseEnabled,
                Threshold = s?.AutoHpThresholdPercent ?? ConsumableRules.DefaultAutoHpThresholdPercent };
        }),
        SoulAuto = value.SoulImprintId.HasValue && value.SoulAutoUseEnabled,
        SoulCondition = value.SoulImprintId.HasValue ? SkillAutoRules.Normalize(value.SoulAutoConditionOverride) : null,
        SoulThreshold = value.SoulImprintId.HasValue ? value.SoulAutoHpThresholdPercent : SkillRules.DefaultAutoHpThresholdPercent
    }));

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string? Code(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
