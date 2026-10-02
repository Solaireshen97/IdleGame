using Game.Server.Data;
using Game.Shared.Dtos.Inventory;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class InventoryReferences
{
    public Dictionary<int, List<InventoryFormationReferenceDto>> Weapons { get; } = [];
    public Dictionary<int, List<InventoryFormationReferenceDto>> Souls { get; } = [];
    public Dictionary<string, List<InventoryFormationReferenceDto>> Stacks { get; } = new(StringComparer.Ordinal);

    public static async Task<InventoryReferences> LoadAsync(GameDbContext db, int characterId, CancellationToken ct = default)
    {
        var index = new InventoryReferences();
        var formations = await db.CharacterBattleFormations.AsNoTracking()
            .Where(f => f.CharacterId == characterId && !f.IsDeleted)
            .Select(f => new { f.Id, f.Name, f.SoulImprintId }).ToListAsync(ct);
        var names = formations.ToDictionary(f => f.Id, f => new InventoryFormationReferenceDto { Id = f.Id, Name = f.Name });
        foreach (var f in formations.Where(f => f.SoulImprintId.HasValue)) Add(index.Souls, f.SoulImprintId!.Value, names[f.Id]);
        var weapons = await (from slot in db.FormationWeaponSlots.AsNoTracking()
            join formation in db.CharacterBattleFormations on slot.FormationId equals formation.Id
            where formation.CharacterId == characterId && !formation.IsDeleted && slot.WeaponId.HasValue
            select new { slot.FormationId, slot.WeaponId }).ToListAsync(ct);
        foreach (var slot in weapons) Add(index.Weapons, slot.WeaponId!.Value, names[slot.FormationId]);
        var stacks = await (from slot in db.FormationConsumableSlots.AsNoTracking()
            join formation in db.CharacterBattleFormations on slot.FormationId equals formation.Id
            where formation.CharacterId == characterId && !formation.IsDeleted && slot.ItemCode != null
            select new { slot.FormationId, slot.ItemCode }).ToListAsync(ct);
        foreach (var slot in stacks) Add(index.Stacks, slot.ItemCode!, names[slot.FormationId]);
        return index;
    }

    private static void Add<T>(Dictionary<T, List<InventoryFormationReferenceDto>> map, T key, InventoryFormationReferenceDto value) where T : notnull
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        if (list.All(x => x.Id != value.Id)) list.Add(value);
    }
}
