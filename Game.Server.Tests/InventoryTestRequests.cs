using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Characters;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Tests;

internal static class InventoryTestRequests
{
    public static async Task<WeaponBatchRequest> WeaponsAsync(GameDbContext db, WeaponCatalog catalog,
        int characterId, string action, params int[] ids)
    {
        var items = await db.CharacterWeapons.AsNoTracking().Include(item => item.Skills)
            .Where(item => item.CharacterId == characterId && ids.Contains(item.Id)).ToListAsync();
        var references = await InventoryReferences.LoadAsync(db, characterId);
        var inRoom = await db.RoomSlots.AsNoTracking().AnyAsync(slot => slot.CharacterId == characterId);
        var preview = InventoryBatchRules.PreviewWeapons(characterId, action, items, catalog, inRoom, references);
        return new()
        {
            WeaponIds = ids.ToList(), RequestId = Guid.NewGuid().ToString("N"),
            ExpectedVersions = preview.ExpectedVersions, OutcomeFingerprint = preview.OutcomeFingerprint
        };
    }

    public static async Task<SoulImprintBatchRequest> SoulsAsync(GameDbContext db, SoulImprintCatalog catalog,
        int characterId, params int[] ids)
    {
        var items = await db.CharacterSoulImprints.AsNoTracking()
            .Where(item => item.CharacterId == characterId && ids.Contains(item.Id)).ToListAsync();
        var references = await InventoryReferences.LoadAsync(db, characterId);
        var inRoom = await db.RoomSlots.AsNoTracking().AnyAsync(slot => slot.CharacterId == characterId);
        var preview = InventoryBatchRules.PreviewSouls(characterId, items, catalog, inRoom, references);
        return new()
        {
            SoulImprintIds = ids.ToList(), RequestId = Guid.NewGuid().ToString("N"),
            ExpectedVersions = preview.ExpectedVersions, OutcomeFingerprint = preview.OutcomeFingerprint
        };
    }
}
