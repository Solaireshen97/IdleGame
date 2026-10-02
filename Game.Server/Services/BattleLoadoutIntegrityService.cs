using Game.Server.Data;
using Game.Shared.Models;

namespace Game.Server.Services;

public sealed class BattleLoadoutIntegrityService(GameDbContext db, CombatLoadoutService loadouts)
{
    // Callers persist this marker with the room version and prohibit every advancement path.
    public async Task<string?> EnsureAsync(Room room, IReadOnlyList<BattleParticipant> party)
    {
        if (room.LoadoutIntegrityError is not null) return room.LoadoutIntegrityError;
        var captured = await loadouts.CaptureManyAsync(party.Where(p => p.Slot.AppliedLoadoutJson is not null).Select(p => p.Character).ToList());
        foreach (var participant in party)
        {
            if (participant.Slot.AppliedLoadoutJson is null) continue;
            try
            {
                var saved = CombatLoadoutCodec.Deserialize(participant.Slot.AppliedLoadoutJson);
                var current = captured[participant.Character.Id];
                if (CombatLoadoutCodec.ConfigurationHash(saved) != CombatLoadoutCodec.ConfigurationHash(current))
                    return room.LoadoutIntegrityError = "LoadoutIntegrityMismatch";
                // Reading also checks the persisted override schema before actions use it.
                BattleAutoPolicyResolver.SoulAuto(participant.Slot, false);
            }
            catch (Exception exception) when (exception is FormatException or System.Text.Json.JsonException or InvalidOperationException)
            {
                return room.LoadoutIntegrityError = "InvalidBattleLoadoutSnapshot";
            }
        }
        return null;
    }
}
