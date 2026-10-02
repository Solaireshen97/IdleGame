using Game.Shared;
using Game.Shared.Dtos.Inventory;
using Game.Shared.Enums;
using Game.Shared.Models;

namespace Game.Server.Services;

/// <summary>Shared eligibility rules. Callers must re-read their inputs inside the write transaction.</summary>
public static class InventoryActionPolicy
{
    public static List<InventoryActionAvailabilityDto> Weapon(CharacterWeapon item, WeaponCatalog catalog,
        bool inRoom, bool referenced)
    {
        var protectedReasons = new List<string>();
        if (inRoom) protectedReasons.Add("LoadoutLocked");
        if (item.EquippedSlotIndex.HasValue) protectedReasons.Add("WeaponEquipped");
        if (item.IsLocked) protectedReasons.Add("WeaponLocked");
        if (referenced) protectedReasons.Add("FormationItemReferenced");
        var growthReasons = inRoom ? new List<string> { "LoadoutLocked" } : [];
        return
        [
            Action(item.IsLocked ? "unlock" : "lock", []),
            Action("sell", protectedReasons),
            Action("dismantle", protectedReasons.Concat(catalog.CanDismantle(item) ? [] : new[] { "WeaponCannotBeDismantled" })),
            Action("consume", protectedReasons.Concat(item.Origin == WeaponOrigin.Starter ? new[] { "StarterWeaponCannotBeConsumed" } : [])),
            Action("enhance", growthReasons.Concat(item.Skills.Any(s => catalog.FindSkill(s.SkillCode) is not null &&
                    s.EnhancementLevel < WeaponRules.EnhancementLimit(item.QualityRank, s.BaseLevel))
                ? [] : new[] { item.Skills.Any(s => catalog.FindSkill(s.SkillCode) is null) ? "UnknownWeaponSkill" : "WeaponSkillAtMaximum" })),
            Action("upgrade", growthReasons.Concat(item.QualityRank >= WeaponRules.MaxQualityBonusLevels ? new[] { "WeaponQualityAtMaximum" } : []))
        ];
    }

    public static List<InventoryActionAvailabilityDto> Soul(CharacterSoulImprint item, SoulImprintCatalog catalog,
        bool inRoom, bool referenced)
    {
        var reasons = new List<string>();
        if (inRoom) reasons.Add("LoadoutLocked");
        if (item.EquippedSlotIndex.HasValue) reasons.Add("SoulImprintEquipped");
        if (item.IsLocked) reasons.Add("SoulImprintLocked");
        if (referenced) reasons.Add("FormationItemReferenced");
        if (catalog.Find(item.SoulImprintCode) is null) reasons.Add("UnknownSoulImprint");
        return [Action(item.IsLocked ? "unlock" : "lock", []), Action("dismantle", reasons)];
    }

    public static InventoryActionAvailabilityDto Action(string action, IEnumerable<string> reasons)
    {
        var list = reasons.Distinct().ToList();
        return new() { Action = action, Allowed = list.Count == 0, ReasonCodes = list };
    }

    public static string? Error(IEnumerable<InventoryEntryDto> entries, string action)
    {
        foreach (var entry in entries)
        {
            var availability = entry.Actions.FirstOrDefault(x => x.Action == action);
            if (availability is null) return "InvalidInventoryAction";
            if (availability.Allowed) continue;
            var reason = availability.ReasonCodes.FirstOrDefault() ?? "InvalidInventoryAction";
            if (reason == "FormationItemReferenced") reason += ":" + string.Join("、", entry.FormationReferences.Select(x => x.Name).Distinct());
            return reason;
        }
        return null;
    }
}
