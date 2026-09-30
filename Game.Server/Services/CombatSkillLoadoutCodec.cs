using System.Text.Json;
using Game.Shared;
using Game.Shared.Models;

namespace Game.Server.Services;

public sealed record CombatSkillLoadoutSlot(int SlotIndex, string? SkillCode, bool AutoUseEnabled,
    string? AutoConditionOverride, int AutoHpThresholdPercent);

public sealed record CombatSkillLoadoutSnapshot(int SchemaVersion, List<CombatSkillLoadoutSlot> Slots);

public sealed class UnsupportedCombatSkillLoadoutVersionException(int version)
    : Exception($"Unsupported combat skill loadout version: {version}");

public static class CombatSkillLoadoutCodec
{
    private const int CurrentVersion = 1;

    public static string Capture(IEnumerable<CharacterSkillSlot> slots) => JsonSerializer.Serialize(
        new CombatSkillLoadoutSnapshot(CurrentVersion, slots.OrderBy(slot => slot.SlotIndex).Select(slot =>
            new CombatSkillLoadoutSlot(slot.SlotIndex, slot.SkillCode, slot.AutoUseEnabled,
                slot.AutoConditionOverride, slot.AutoHpThresholdPercent)).ToList()));

    public static void EnsureSupportedVersion(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var document = JsonDocument.Parse(json);
            EnsureSupportedVersion(document.RootElement);
        }
        catch (JsonException) { /* Damaged data is recoverable; future versions are not. */ }
    }

    private static void EnsureSupportedVersion(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(nameof(CombatSkillLoadoutSnapshot.SchemaVersion), out var version) &&
            version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number != CurrentVersion)
            throw new UnsupportedCombatSkillLoadoutVersionException(number);
    }

    public static IReadOnlyList<CombatSkillLoadoutSlot> Restore(string? json, Character character,
        SkillCatalog catalog, IReadOnlyDictionary<string, int> professionLevels)
    {
        List<CombatSkillLoadoutSlot>? slots = null;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                    slots = JsonSerializer.Deserialize<List<CombatSkillLoadoutSlot>>(json);
                else if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    EnsureSupportedVersion(document.RootElement);
                    var snapshot = JsonSerializer.Deserialize<CombatSkillLoadoutSnapshot>(json);
                    if (snapshot?.SchemaVersion == CurrentVersion) slots = snapshot.Slots;
                }
            }
            catch (JsonException) { /* Historical or damaged data must not prevent switching. */ }
        }

        if (slots is null || slots.Count > SkillRules.SlotCount ||
            slots.Any(slot => slot is null || slot.SlotIndex < 1 || slot.SlotIndex > SkillRules.SlotCount) ||
            slots.Select(slot => slot.SlotIndex).Distinct().Count() != slots.Count)
            slots = catalog.NativeSkills(character).Take(SkillRules.SlotCount).Select((skill, index) =>
                new CombatSkillLoadoutSlot(index + 1, skill.Code, false, null,
                    SkillRules.DefaultAutoHpThresholdPercent)).ToList();

        var equipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sharedEquipped = false;
        return slots.OrderBy(slot => slot.SlotIndex).Select(slot =>
        {
            var skill = catalog.Resolve(character, slot.SkillCode?.Trim(), professionLevels);
            if (skill is null || !equipped.Add(skill.Code) || skill.IsShared && sharedEquipped)
                return new CombatSkillLoadoutSlot(slot.SlotIndex, null, false, null,
                    SkillRules.DefaultAutoHpThresholdPercent);
            if (skill.IsShared) sharedEquipped = true;
            var condition = SkillAutoRules.Normalize(slot.AutoConditionOverride);
            return new CombatSkillLoadoutSlot(slot.SlotIndex, skill.Code, slot.AutoUseEnabled,
                SkillAutoRules.IsValidOverride(condition) ? condition : null,
                slot.AutoHpThresholdPercent is >= 1 and <= 100 ? slot.AutoHpThresholdPercent :
                    SkillRules.DefaultAutoHpThresholdPercent);
        }).ToList();
    }
}
