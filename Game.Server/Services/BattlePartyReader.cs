using Game.Server.Data;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

/// <summary>Materialize tracked actors before filtering: SQL cannot see this round's unsaved HP or formation changes.</summary>
public static class BattlePartyReader
{
    public static async Task<List<BattleParticipant>> ReadAsync(GameDbContext db, int roomId)
    {
        var rows = await (from slot in db.RoomSlots
            join character in db.Characters on slot.CharacterId equals character.Id
            where slot.RoomId == roomId
            select new BattleParticipant(slot, character)).ToListAsync();
        var party = rows.ToDictionary(p => p.Slot);
        foreach (var slot in db.RoomSlots.Local.Where(s => s.RoomId == roomId && s.CharacterId.HasValue))
            if (await db.Characters.FindAsync(slot.CharacterId!.Value) is { } character)
                party[slot] = new(slot, character);
        return party.Values.Where(p => p.Slot.RoomId == roomId && p.Slot.CharacterId == p.Character.Id &&
                db.Entry(p.Slot).State != EntityState.Deleted && db.Entry(p.Character).State != EntityState.Deleted)
            .OrderBy(p => p.Slot.SlotIndex).ToList();
    }
}
