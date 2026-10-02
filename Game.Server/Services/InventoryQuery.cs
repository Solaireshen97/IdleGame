using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Dtos.Inventory;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class InventoryQuery(GameDbContext db, UserService users, ItemDefinitionCatalog definitions,
    WeaponCatalog weapons, SoulImprintCatalog souls, WeaponBreakthroughCatalog breakthroughs)
{
    public async Task<(InventoryOverviewResponse? Response, string? Error)> GetAsync(string? token, int characterId,
        InventoryQueryRequest query, CancellationToken ct = default)
    {
        if (!Valid(query)) return (null, "InvalidInventoryQuery");
        var (user, authError) = await users.GetCurrentUserEntityAsync(token);
        if (authError is not null) return (null, authError);
        await using var read = await InventoryReadScope.BeginAsync(db, ct);
        var (character, error) = await OwnedAsync(user!.Id, characterId, ct);
        if (error is not null) return (null, error);
        var snapshot = await LoadAsync(character!, ct);
        var pending = await PendingAsync(characterId, snapshot.GeneratedAtUtc, ct);
        var rows = Filter(snapshot.Entries, query).ToList();
        var page = Math.Min(query.Page, Math.Max(1, (rows.Count + query.PageSize - 1) / query.PageSize));
        return (new()
        {
            CharacterId = characterId, CharacterName = character!.Name, Gold = character.Gold,
            GeneratedAtUtc = snapshot.GeneratedAtUtc, IsLoadoutLocked = snapshot.InRoom,
            WeaponCount = snapshot.Weapons.Count, SoulImprintCount = snapshot.Souls.Count,
            OwnedStackKinds = snapshot.Entries.LongCount(x => x.AssetKind == InventoryKinds.Stack),
            EquippedCount = snapshot.Entries.LongCount(x => x.AssetKind != InventoryKinds.Stack && x.IsEquipped),
            ProtectedCount = snapshot.Entries.LongCount(x => x.AssetKind != InventoryKinds.Stack &&
                (x.IsEquipped || x.IsLocked || x.FormationReferences.Count > 0)),
            Categories = InventoryCategories.Values.Select(category => new InventoryCategorySummary
            {
                Category = category, RowCount = snapshot.Entries.LongCount(x => x.Category == category),
                Quantity = snapshot.Entries.Where(x => x.Category == category).Sum(x => (long)x.Quantity),
                Unit = category == InventoryCategories.Weapons ? "件" : category == InventoryCategories.SoulImprints ? "枚"
                    : category == InventoryCategories.Other ? "项" : "种"
            }).ToList(),
            FilteredRowCount = rows.Count, Page = page, PageSize = query.PageSize,
            Items = rows.Skip((page - 1) * query.PageSize).Take(query.PageSize).ToList(), Pending = pending
        }, null);
    }

    public async Task<(InventoryItemDetailDto? Response, string? Error)> DetailAsync(string? token, int characterId,
        string assetKind, string key, CancellationToken ct = default)
    {
        var (user, authError) = await users.GetCurrentUserEntityAsync(token);
        if (authError is not null) return (null, authError);
        if (assetKind is not (InventoryKinds.Weapon or InventoryKinds.SoulImprint or InventoryKinds.Stack) || string.IsNullOrEmpty(key))
            return (null, "InvalidInventoryQuery");
        await using var read = await InventoryReadScope.BeginAsync(db, ct);
        var (character, error) = await OwnedAsync(user!.Id, characterId, ct);
        if (error is not null) return (null, error);
        var snapshot = await LoadAsync(character!, ct);
        var stableKey = key.StartsWith(assetKind + ":", StringComparison.Ordinal) ? key : assetKind + ":" + key;
        var entry = snapshot.Entries.SingleOrDefault(x => x.AssetKind == assetKind && x.Key == stableKey);
        if (entry is null) return (null, "InventoryItemNotFound");
        var response = new InventoryItemDetailDto { CharacterId = characterId, GeneratedAtUtc = snapshot.GeneratedAtUtc, Entry = entry };
        var main = snapshot.Weapons.FirstOrDefault(w => w.EquippedSlotIndex == WeaponRules.MainSlotIndex)?.Element;
        if (assetKind == InventoryKinds.Weapon)
        {
            var weapon = await db.CharacterWeapons.AsNoTracking().Include(w => w.Skills)
                .SingleAsync(w => w.CharacterId == characterId && w.Id == entry.InstanceId, ct);
            response.Weapon = InventoryItemProjection.Weapon(weapon, weapons, breakthroughs, main);
            var normalized = weapons.FindItem(weapon.WeaponCode)?.Code ?? weapon.WeaponCode;
            var candidates = snapshot.Weapons.Where(w => w.Id != weapon.Id && string.Equals(
                    weapons.FindItem(w.WeaponCode)?.Code ?? w.WeaponCode, normalized, StringComparison.OrdinalIgnoreCase))
                .Where(w => InventoryActionPolicy.Weapon(w, weapons, snapshot.InRoom, snapshot.References.Weapons.ContainsKey(w.Id))
                    .Any(a => a.Action == "consume" && a.Allowed)).OrderBy(w => w.QualityRank).ThenBy(w => w.Id);
            response.QualityMaterials = candidates.Select(w => InventoryItemProjection.Weapon(w, weapons, breakthroughs, main)).ToList();
        }
        if (assetKind == InventoryKinds.SoulImprint)
            response.SoulImprint = InventoryItemProjection.Soul(snapshot.Souls.Single(s => s.Id == entry.InstanceId), souls);
        response.Fragments = snapshot.Stacks.Where(s => s.ItemCode.StartsWith("weapon-fragment-t", StringComparison.Ordinal))
            .Select(s => new WeaponFragmentResponse { Code = s.ItemCode, Name = definitions.DescribeStack(s.ItemCode, character!).Name,
                Tier = definitions.DescribeStack(s.ItemCode, character!).Tier ?? 1, Quantity = s.Quantity }).ToList();
        response.BreakthroughMaterials = breakthroughs.Recipes.Select(recipe =>
        {
            var fragments = snapshot.Stacks.FirstOrDefault(s => s.ItemCode == recipe.FragmentCode)?.Quantity ?? 0;
            var stones = snapshot.Stacks.FirstOrDefault(s => s.ItemCode == recipe.StoneCode)?.Quantity ?? 0;
            return new WeaponBreakthroughMaterialResponse
            {
                Tier = recipe.Tier, FragmentCode = recipe.FragmentCode, FragmentName = definitions.DescribeStack(recipe.FragmentCode, character!).Name,
                FragmentQuantity = fragments, StoneCode = recipe.StoneCode, StoneName = definitions.DescribeStack(recipe.StoneCode, character!).Name,
                StoneQuantity = stones, FragmentsPerStone = recipe.FragmentsPerStone,
                CanCraftQuantity = Math.Min(fragments / recipe.FragmentsPerStone, int.MaxValue - stones)
            };
        }).ToList();
        return (response, null);
    }

    public async Task<(InventoryActionPreviewResponse? Response, string? Error)> PreviewAsync(string? token, int characterId,
        InventoryActionPreviewRequest request, CancellationToken ct = default)
    {
        var (user, authError) = await users.GetCurrentUserEntityAsync(token);
        if (authError is not null) return (null, authError);
        if (!InventoryBatchRules.ValidSelection(request.InstanceIds)) return (null, "InvalidInventorySelection");
        if (request.AssetKind == InventoryKinds.Weapon ? request.Action is not ("sell" or "dismantle")
            : request.AssetKind != InventoryKinds.SoulImprint || request.Action != "dismantle") return (null, "InvalidInventoryAction");
        await using var read = await InventoryReadScope.BeginAsync(db, ct);
        var (character, error) = await OwnedAsync(user!.Id, characterId, ct);
        if (error is not null) return (null, error);
        var inRoom = await db.RoomSlots.AnyAsync(s => s.CharacterId == characterId, ct);
        var references = await InventoryReferences.LoadAsync(db, characterId, ct);
        if (request.AssetKind == InventoryKinds.Weapon)
        {
            var selected = await db.CharacterWeapons.AsNoTracking().Include(w => w.Skills)
                .Where(w => w.CharacterId == characterId && request.InstanceIds.Contains(w.Id)).ToListAsync(ct);
            if (selected.Count != request.InstanceIds.Count) return (null, "WeaponNotOwned");
            return (InventoryBatchRules.PreviewWeapons(characterId, request.Action, selected, weapons, inRoom, references), null);
        }
        var selectedSouls = await db.CharacterSoulImprints.AsNoTracking()
            .Where(s => s.CharacterId == characterId && request.InstanceIds.Contains(s.Id)).ToListAsync(ct);
        if (selectedSouls.Count != request.InstanceIds.Count) return (null, "SoulImprintNotOwned");
        return (InventoryBatchRules.PreviewSouls(characterId, selectedSouls, souls, inRoom, references), null);
    }

    private async Task<(Character? Character, string? Error)> OwnedAsync(int userId, int id, CancellationToken ct)
    {
        var character = await db.Characters.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        return character is null ? (null, "CharacterNotFound") : character.UserId != userId ? (null, "NotOwner") : (character, null);
    }

    private async Task<Snapshot> LoadAsync(Character character, CancellationToken ct)
    {
        var stocks = await db.CharacterItemStacks.AsNoTracking().Where(s => s.CharacterId == character.Id).ToListAsync(ct);
        var items = await db.CharacterWeapons.AsNoTracking().Where(w => w.CharacterId == character.Id).ToListAsync(ct);
        // Only progression scalars are needed for eligibility and recycling, never the full skill descriptions.
        var skills = await (from skill in db.CharacterWeaponSkills.AsNoTracking()
            join weapon in db.CharacterWeapons on skill.WeaponId equals weapon.Id
            where weapon.CharacterId == character.Id
            select new CharacterWeaponSkill { Id = skill.Id, WeaponId = skill.WeaponId, SlotIndex = skill.SlotIndex,
                SkillCode = skill.SkillCode, Level = skill.Level, BaseLevel = skill.BaseLevel,
                EnhancementLevel = skill.EnhancementLevel, SpentFragments = skill.SpentFragments }).ToListAsync(ct);
        var skillIndex = skills.ToLookup(s => s.WeaponId);
        foreach (var item in items) item.Skills = skillIndex[item.Id].ToList();
        var imprints = await db.CharacterSoulImprints.AsNoTracking().Where(s => s.CharacterId == character.Id).ToListAsync(ct);
        var inRoom = await db.RoomSlots.AnyAsync(s => s.CharacterId == character.Id, ct);
        var references = await InventoryReferences.LoadAsync(db, character.Id, ct);
        var slots = await db.CharacterConsumableSlots.AsNoTracking().Where(s => s.CharacterId == character.Id && s.ItemCode != null).ToListAsync(ct);
        var codeConflicts = stocks.GroupBy(s => s.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = new List<InventoryEntryDto>();
        entries.AddRange(items.Select(w => InventoryItemProjection.WeaponEntry(w, weapons, inRoom, references.Weapons.GetValueOrDefault(w.Id))));
        entries.AddRange(imprints.Select(s => InventoryItemProjection.SoulEntry(s, souls, inRoom, references.Souls.GetValueOrDefault(s.Id))));
        foreach (var stack in stocks.Where(s => s.Quantity > 0))
        {
            var row = definitions.DescribeStack(stack.ItemCode, character);
            row.Key = "stack:" + stack.ItemCode; row.AssetKind = InventoryKinds.Stack; row.Code = stack.ItemCode;
            row.Version = stack.Version; row.Quantity = stack.Quantity;
            row.FormationReferences = references.Stacks.GetValueOrDefault(stack.ItemCode) ?? [];
            row.EquippedSlotIndex = slots.FirstOrDefault(s => s.ItemCode == stack.ItemCode)?.SlotIndex;
            row.IsEquipped = row.EquippedSlotIndex.HasValue;
            row.HasCodeConflict = codeConflicts.Contains(stack.ItemCode);
            if (row.HasCodeConflict) { row.Tags.Add("物品代码冲突"); row.Usages.Clear(); }
            var recipe = breakthroughs.Recipes.FirstOrDefault(r => r.FragmentCode == stack.ItemCode || r.StoneCode == stack.ItemCode);
            if (recipe is not null)
            {
                var reasons = new List<string>();
                if (inRoom) reasons.Add("LoadoutLocked");
                if (codeConflicts.Contains(recipe.FragmentCode) || codeConflicts.Contains(recipe.StoneCode) || stocks.Any(s =>
                    (string.Equals(s.ItemCode, recipe.FragmentCode, StringComparison.OrdinalIgnoreCase) && s.ItemCode != recipe.FragmentCode) ||
                    (string.Equals(s.ItemCode, recipe.StoneCode, StringComparison.OrdinalIgnoreCase) && s.ItemCode != recipe.StoneCode)))
                    reasons.Add("InventoryCodeConflict");
                if ((stocks.FirstOrDefault(s => s.ItemCode == recipe.FragmentCode)?.Quantity ?? 0) < recipe.FragmentsPerStone) reasons.Add("InsufficientBreakthroughFragments");
                if (stocks.FirstOrDefault(s => s.ItemCode == recipe.StoneCode)?.Quantity == int.MaxValue) reasons.Add("InventoryFull");
                row.Actions.Add(InventoryActionPolicy.Action("craft", reasons));
            }
            entries.Add(row);
        }
        return new(items, imprints, stocks, entries, references, inRoom, DateTime.UtcNow);
    }

    private async Task<InventoryPendingSummary> PendingAsync(int characterId, DateTime now, CancellationToken ct)
    {
        var rewards = await (from entry in db.RewardEntries.AsNoTracking()
            join run in db.RewardRuns on new { entry.RoomId, entry.Sequence } equals new { run.RoomId, run.Sequence }
            where entry.CharacterId == characterId && run.Status == "Pending"
            select new { entry.RoomId, entry.Sequence, entry.Kind, entry.Code, entry.Quantity }).ToListAsync(ct);
        return new()
        {
            MaturePlotCount = await db.CharacterGardenPlots.CountAsync(p => p.CharacterId == characterId && p.MaturesAtUtc != null && p.MaturesAtUtc <= now, ct),
            PendingBattleCount = rewards.Select(r => (r.RoomId, r.Sequence)).Distinct().Count(),
            PendingGold = rewards.Where(r => r.Kind == "Gold").Sum(r => (long)r.Quantity),
            BattleRewards = rewards.Where(r => r.Kind is "Weapon" or "SoulImprint" or "Consumable" or "Material")
                .GroupBy(r => (r.Kind, r.Code)).Select(g => new InventoryRewardDto { Kind = g.Key.Kind, Code = g.Key.Code,
                    Name = definitions.DescribeReward(g.Key.Kind, g.Key.Code), Quantity = g.Sum(r => (long)r.Quantity) }).ToList()
        };
    }

    private static bool Valid(InventoryQueryRequest q) =>
        (q.Category == InventoryCategories.All || InventoryCategories.Values.Contains(q.Category)) &&
        q.State is "all" or "equipped" or "unequipped" or "locked" or "referenced" or "protected" or "available" &&
        q.Sort is "name" or "quantity" or "level" or "quality" && q.Page > 0 && q.PageSize is >= 1 and <= 100 &&
        q.Search?.Length is not > 100 && (q.Element is null || Enum.IsDefined(q.Element.Value)) &&
        q.Tier is not <= 0 && q.QualityRank is not < 0 and not > WeaponRules.MaxQualityBonusLevels;

    private static IEnumerable<InventoryEntryDto> Filter(IEnumerable<InventoryEntryDto> entries, InventoryQueryRequest q)
    {
        var search = q.Search?.Trim();
        var filtered = entries.Where(x => (q.Category == InventoryCategories.All || x.Category == q.Category) &&
            (string.IsNullOrEmpty(search) || x.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || x.Code.Contains(search, StringComparison.OrdinalIgnoreCase)) &&
            (!q.Element.HasValue || x.Element == q.Element) && (!q.Tier.HasValue || x.Tier == q.Tier) &&
            (!q.QualityRank.HasValue || x.QualityRank == q.QualityRank) && (q.State switch
            {
                "equipped" => x.IsEquipped, "unequipped" => !x.IsEquipped, "locked" => x.IsLocked,
                "referenced" => x.FormationReferences.Count > 0,
                "protected" => x.IsEquipped || x.IsLocked || x.FormationReferences.Count > 0,
                "available" => x.Actions.Any(a => a.Allowed && a.Action is "sell" or "dismantle" or "consume"), _ => true
            }));
        var ordered = q.Sort switch
        {
            "quantity" => filtered.OrderByDescending(x => x.Quantity),
            "level" => filtered.OrderByDescending(x => x.ItemLevel ?? x.Tier ?? 0),
            "quality" => filtered.OrderByDescending(x => x.QualityRank ?? -1),
            _ => filtered.OrderBy(x => x.Name, StringComparer.Ordinal)
        };
        return ordered.ThenBy(x => x.Key, StringComparer.Ordinal);
    }

    private sealed record Snapshot(List<CharacterWeapon> Weapons, List<CharacterSoulImprint> Souls,
        List<CharacterItemStack> Stacks, List<InventoryEntryDto> Entries, InventoryReferences References,
        bool InRoom, DateTime GeneratedAtUtc);
}
