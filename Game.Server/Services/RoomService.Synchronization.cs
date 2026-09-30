using Game.Shared.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public partial class RoomService
{
    public async Task<(RoomDetailResponse? Room, string ProjectionId)> GetSynchronizedRoomAsync(
        int roomId, string? token, RoomProjectionCache? cache)
    {
        // Authentication, active character and visibility are always fresh, even on hits.
        var room = await dbContext.Rooms.FirstOrDefaultAsync(item => item.Id == roomId);
        if (room is null) return (null, "");
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (null, "");
        var slots = await dbContext.RoomSlots.AsNoTracking().Where(slot => slot.RoomId == roomId).ToListAsync();
        if ((room.ClosedAtUtc.HasValue || !room.IsPublic) && room.OwnerUserId != user!.Id &&
            !slots.Any(slot => slot.UserId == user.Id)) return (null, "");

        var now = DateTime.UtcNow;
        var offlineMask = slots.Where(slot => RoomAutoPolicy.IsOffline(room, slot, now))
            .Aggregate(0, (mask, slot) => mask | (1 << slot.SlotIndex));
        var key = new RoomProjectionCache.Key(roomId, user!.Id, user.ActiveCharacterId);
        var revision = cache?.Revision ?? 0;
        var cached = dbContext.Database.CurrentTransaction is null ? cache?.Get(key, room.Version, offlineMask) : null;
        var detail = cached?.Room ?? await BuildRoomDetailAsync(room, user.Id, includeRewardDetails: false);
        if (detail is null) return (null, "");
        var id = cached?.Id ?? "";
        if (cached is null && cache is not null && dbContext.Database.CurrentTransaction is null)
        {
            var projectedOfflineMask = detail.Slots.Where(slot => slot.IsOffline)
                .Aggregate(0, (mask, slot) => mask | (1 << slot.SlotIndex));
            id = cache.Store(key, detail, projectedOfflineMask, revision);
        }
        detail.ServerTimeUtc = DateTime.UtcNow;
        var history = battleLogStore?.GetSnapshot(roomId);
        detail.BattleHistoryEpoch = history?.Epoch ?? "";
        detail.BattleLogs = history?.Logs ?? [];
        detail.BattleEvents = history?.Events.Where(fact => fact.SettlementVersion <= detail.RoomVersion).ToList() ?? [];
        return (detail, id);
    }
}
