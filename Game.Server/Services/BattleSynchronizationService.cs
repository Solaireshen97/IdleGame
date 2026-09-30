using Game.Server.Data;
using Game.Shared.Dtos;

namespace Game.Server.Services;

public sealed class BattleSynchronizationService(GameDbContext db, BattleService battles, RoomService rooms,
    RoomProjectionCache? cache = null)
{
    public async Task<(BattleSyncResponse? Response, string? Error)> SynchronizeAsync(BattleSyncRequest request, string? token)
    {
        // Preserve presence and automatic progression. Spectators and closed-room
        // readers still go through the normal room visibility check below.
        var (_, error) = await battles.SyncAsync(request.RoomId, token);
        if (error is not (null or "NotInRoom" or "RoomClosed" or "ConcurrencyConflict")) return (null, error);
        db.ChangeTracker.Clear();
        var (room, projectionId) = await rooms.GetSynchronizedRoomAsync(request.RoomId, token, cache);
        if (room is null) return (null, "NotFound");
        var response = CreateResponse(room, request);
        response.ProjectionId = projectionId;
        if (projectionId.Length > 0 && projectionId == request.ProjectionId)
        {
            response.Unchanged = new()
            {
                RoomId = room.RoomId, RoomVersion = room.RoomVersion, ServerTimeUtc = room.ServerTimeUtc,
                BattleHistoryEpoch = room.BattleHistoryEpoch, BattleEvents = room.BattleEvents, BattleLogs = room.BattleLogs
            };
            response.Room = null;
        }
        return (response, null);
    }

    public static BattleSyncResponse CreateResponse(RoomDetailResponse room, BattleSyncRequest request)
    {
        var lastEvent = room.BattleEvents.Select(item => item.Id).DefaultIfEmpty().Max();
        var lastLog = room.BattleLogs.Select(item => item.Id).DefaultIfEmpty().Max();
        // IDs are process-wide. An absent cursor before the retained window
        // conservatively resends it; settlement IDs prevent duplicate playback.
        var reset = request.HistoryEpoch != room.BattleHistoryEpoch ||
            OutsideWindow(request.AfterEventId, room.BattleEvents.Select(item => item.Id)) ||
            OutsideWindow(request.AfterLogId, room.BattleLogs.Select(item => item.Id));
        if (!reset)
        {
            room.BattleEvents = room.BattleEvents.Where(item => item.Id > request.AfterEventId).ToList();
            room.BattleLogs = room.BattleLogs.Where(item => item.Id > request.AfterLogId).ToList();
        }
        return new() { Room = room, HistoryReset = reset, LastEventId = lastEvent, LastLogId = lastLog };
    }

    private static bool OutsideWindow(long cursor, IEnumerable<long> values)
    {
        if (cursor <= 0) return false;
        var ids = values.ToArray();
        return ids.Length == 0 || cursor < ids.Min() || cursor > ids.Max();
    }
}
