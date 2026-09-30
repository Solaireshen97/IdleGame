using Game.Shared.Dtos;

namespace Game.Client.Services;

public static class BattleSnapshotProjection
{
    public static RoomDetailResponse? Resolve(RoomDetailResponse? baseline, string projectionId, BattleSyncResponse response)
    {
        if (response.Room is not null) return response.Room;
        if (baseline is null || string.IsNullOrEmpty(projectionId) || projectionId != response.ProjectionId ||
            response.Unchanged is not { } history || history.RoomId != baseline.RoomId || history.RoomVersion != baseline.RoomVersion)
            return null;
        return baseline.WithBattleHistory(history);
    }
}
