using Game.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public static class FormationItemReferencePolicy
{
    public static async Task<string?> WeaponsAsync(GameDbContext db, int characterId, IReadOnlyCollection<int> ids)
    {
        var names = await (from slot in db.FormationWeaponSlots
            join formation in db.CharacterBattleFormations on slot.FormationId equals formation.Id
            where formation.CharacterId == characterId && !formation.IsDeleted &&
                slot.WeaponId.HasValue && ids.Contains(slot.WeaponId.Value)
            select formation.Name).Distinct().ToListAsync();
        return Error(names);
    }

    public static async Task<string?> SoulImprintsAsync(GameDbContext db, int characterId, IReadOnlyCollection<int> ids)
    {
        var names = await db.CharacterBattleFormations.Where(formation => formation.CharacterId == characterId &&
            !formation.IsDeleted && formation.SoulImprintId.HasValue && ids.Contains(formation.SoulImprintId.Value))
            .Select(formation => formation.Name).Distinct().ToListAsync();
        return Error(names);
    }

    private static string? Error(List<string> names) => names.Count == 0 ? null :
        "FormationItemReferenced:" + string.Join("、", names.OrderBy(name => name));
}
