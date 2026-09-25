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

    public static bool IsEnabled(Room room, RoomSlot slot, IReadOnlyCollection<RoomSlot> roomSlots) =>
        (slot.UserId == room.OwnerUserId
            ? roomSlots.FirstOrDefault(candidate => candidate.UserId == room.OwnerUserId && candidate.IsMainControl) ?? slot
            : slot).IsAutoEnabled;

    public static bool IsAuto(Room room, RoomSlot slot, IReadOnlyCollection<int> clearedCharacterIds,
        IReadOnlyCollection<RoomSlot> roomSlots) =>
        slot.CharacterId.HasValue &&
        clearedCharacterIds.Contains(slot.CharacterId.Value) && IsEnabled(room, slot, roomSlots);
}
