using Game.Shared;
using Game.Shared.Models;

namespace Game.Server.Services;

internal static class RoomAutoPolicy
{
    public static bool IsOffline(Room room, RoomSlot slot, DateTime now)
    {
        if (!slot.CharacterId.HasValue) return false;
        var lastSeen = slot.LastSeenAtUtc ?? room.StartedAtUtc;
        return lastSeen.HasValue && now >= lastSeen.Value.AddSeconds(BattleRules.PresenceTimeoutSeconds);
    }

    public static bool IsAuto(Room room, RoomSlot slot, IReadOnlyCollection<int> clearedCharacterIds) =>
        slot.CharacterId.HasValue &&
        clearedCharacterIds.Contains(slot.CharacterId.Value) &&
        (slot.IsAutoEnabled || slot.UserId == room.OwnerUserId && !slot.IsMainControl);
}
