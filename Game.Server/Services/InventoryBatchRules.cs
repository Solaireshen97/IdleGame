using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos.Inventory;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public static class InventoryBatchRules
{
    public static bool ValidSelection(IReadOnlyCollection<int>? ids) =>
        ids is { Count: > 0 and <= 100 } && ids.All(i => i > 0) && ids.Distinct().Count() == ids.Count;

    public static InventoryActionPreviewResponse PreviewWeapons(int characterId, string action,
        List<CharacterWeapon> items, WeaponCatalog catalog, bool inRoom, InventoryReferences references)
    {
        var rows = items.OrderBy(w => w.Id).Select(w => InventoryItemProjection.WeaponEntry(w, catalog, inRoom, references.Weapons.GetValueOrDefault(w.Id))).ToList();
        List<InventoryRewardDto> rewards = action == "sell"
            ? [new() { Kind = "Gold", Code = "gold", Name = "金币", Quantity = items.Sum(w => (long)w.SellGold) }]
            : items.GroupBy(w => WeaponRules.FragmentTier(w.ItemLevel)).OrderBy(g => g.Key)
                .Select(g => new InventoryRewardDto { Kind = "Material", Code = WeaponRules.FragmentCode(g.Key),
                    Name = WeaponRules.FragmentName(g.Key), Quantity = g.Sum(w => (long)catalog.DismantleReturn(w)) }).ToList();
        return Preview(characterId, InventoryKinds.Weapon, action, rows, rewards);
    }

    public static InventoryActionPreviewResponse PreviewSouls(int characterId, List<CharacterSoulImprint> items,
        SoulImprintCatalog catalog, bool inRoom, InventoryReferences references)
    {
        var rows = items.OrderBy(s => s.Id).Select(s => InventoryItemProjection.SoulEntry(s, catalog, inRoom, references.Souls.GetValueOrDefault(s.Id))).ToList();
        var rewards = items.Select(s => catalog.Find(s.SoulImprintCode)).Where(d => d is not null).GroupBy(d => d!.Tier)
            .OrderBy(g => g.Key).Select(g => new InventoryRewardDto { Kind = "Material", Code = WeaponRules.FragmentCode(g.Key),
                Name = WeaponRules.FragmentName(g.Key), Quantity = g.Sum(d => (long)d!.DismantleFragments) }).ToList();
        return Preview(characterId, InventoryKinds.SoulImprint, "dismantle", rows, rewards);
    }

    private static InventoryActionPreviewResponse Preview(int characterId, string kind, string action,
        List<InventoryEntryDto> rows, List<InventoryRewardDto> rewards)
    {
        var versions = rows.Select(r => new InventoryInstanceVersion { Id = r.InstanceId!.Value, Version = r.Version }).ToList();
        return new()
        {
            CharacterId = characterId, AssetKind = kind, Action = action, Items = rows,
            Allowed = InventoryActionPolicy.Error(rows, action) is null, ExpectedVersions = versions,
            Rewards = rewards.Where(r => r.Quantity > 0).ToList(),
            OutcomeFingerprint = Hash(JsonSerializer.Serialize(new { characterId, kind, action, versions,
                rewards = rewards.Where(r => r.Quantity > 0).Select(r => new { r.Kind, r.Code, r.Quantity }) }))
        };
    }

    public static string? ValidateConfirmation(string? requestId, IReadOnlyCollection<int> ids,
        List<InventoryInstanceVersion>? expected, string? outcome, InventoryActionPreviewResponse current)
    {
        if (!Guid.TryParse(requestId, out var guid) || guid == Guid.Empty) return "InvalidRequestId";
        if (expected is null || expected.Any(v => v is null) || expected.Count != ids.Count || expected.Select(v => v.Id).Distinct().Count() != ids.Count ||
            expected.Any(v => !ids.Contains(v.Id) || v.Version < 0) || string.IsNullOrWhiteSpace(outcome)) return "InventoryPreviewRequired";
        if (current.ExpectedVersions.Any(v => expected.Single(e => e.Id == v.Id).Version != v.Version) ||
            current.OutcomeFingerprint != outcome) return "InventoryPreviewChanged";
        return null;
    }

    public static string RequestFingerprint(string kind, string action, IReadOnlyCollection<int> ids,
        List<InventoryInstanceVersion>? versions, string? outcome) => Hash(JsonSerializer.Serialize(new
        {
            kind, action, ids = ids.Order().ToArray(),
            versions = versions?.OrderBy(v => v.Id).Select(v => new { v.Id, v.Version }), outcome
        }));

    public static async Task<string?> CreditAsync(GameDbContext db, Character character, IReadOnlyList<InventoryRewardDto> rewards)
    {
        var gold = rewards.Where(r => r.Kind == "Gold").Sum(r => r.Quantity);
        if (gold < 0 || gold > int.MaxValue - (long)character.Gold) return "InventoryFull";
        var materials = rewards.Where(r => r.Kind == "Material").ToList();
        var codes = materials.Select(r => r.Code).ToList();
        var stacks = await db.CharacterItemStacks.Where(s => s.CharacterId == character.Id &&
            codes.Contains(EF.Functions.Collate(s.ItemCode, "NOCASE"))).ToListAsync();
        // Recycling must not add a second spelling of an existing legacy material,
        // nor consume instances while the receiving balance is ambiguous.
        if (stacks.Any(s => !codes.Contains(s.ItemCode, StringComparer.Ordinal)) ||
            stacks.GroupBy(s => s.ItemCode, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            return "InventoryCodeConflict";
        if (materials.Any(r => r.Quantity < 0 || r.Quantity > int.MaxValue - (long)(stacks.SingleOrDefault(s => s.ItemCode == r.Code)?.Quantity ?? 0)))
            return "InventoryFull";
        character.Gold += (int)gold;
        character.Version++;
        foreach (var reward in materials)
        {
            var stack = stacks.SingleOrDefault(s => s.ItemCode == reward.Code);
            if (stack is null) db.CharacterItemStacks.Add(new() { CharacterId = character.Id, ItemCode = reward.Code, Quantity = (int)reward.Quantity });
            else { stack.Quantity += (int)reward.Quantity; stack.Version++; }
        }
        return null;
    }

    public static InventoryOperationResult Receipt(string requestId, InventoryActionPreviewResponse preview) => new()
    {
        RequestId = requestId, AssetKind = preview.AssetKind, Action = preview.Action,
        InstanceIds = preview.ExpectedVersions.Select(v => v.Id).ToList(), Rewards = preview.Rewards
    };

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
