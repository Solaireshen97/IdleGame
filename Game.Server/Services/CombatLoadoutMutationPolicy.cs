using Game.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public static class CombatLoadoutMutationPolicy
{
    public static async Task<string?> LockErrorAsync(GameDbContext db, int characterId) =>
        await db.RoomSlots.AnyAsync(slot => slot.CharacterId == characterId) ? "LoadoutLocked" : null;
}
