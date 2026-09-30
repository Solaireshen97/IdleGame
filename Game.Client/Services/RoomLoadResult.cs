using Game.Shared.Dtos;

namespace Game.Client.Services;

public enum RoomLoadStatus
{
    Success,
    Unavailable,
    Unauthorized,
    RetryableError
}

public sealed record RoomLoadResult(RoomDetailResponse? Room, RoomLoadStatus Status, BattleSyncResponse? Synchronization = null);
