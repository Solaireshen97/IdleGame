using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Game.Shared.Dtos;

namespace Game.Client.Services;

public partial class ApiService
{
    public async Task<RoomLoadResult> LoadBattleSnapshotAsync(BattleSyncRequest value, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = await CreateRequestAsync(HttpMethod.Post, "api/battle/snapshot", requiresAuth: true, scope: ApiRequestScope.Room);
            request.Content = JsonContent.Create(value);
            using var response = await SendTrackedAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(null, RoomLoadStatus.Unauthorized);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
                return new(null, RoomLoadStatus.Unavailable);
            if (!response.IsSuccessStatusCode) return new(null, RoomLoadStatus.RetryableError);
            var snapshot = await response.Content.ReadFromJsonAsync<BattleSyncResponse>(timeout.Token);
            if (snapshot is null || !IsRequestContextCurrent(request)) return new(null, RoomLoadStatus.RetryableError);
            var roomId = snapshot.Room?.RoomId ?? snapshot.Unchanged?.RoomId;
            if (roomId != value.RoomId) return new(null, RoomLoadStatus.Unavailable);
            if (snapshot.Room is null && (string.IsNullOrEmpty(value.ProjectionId) || snapshot.ProjectionId != value.ProjectionId))
                return new(null, RoomLoadStatus.RetryableError);
            return new(snapshot.Room, RoomLoadStatus.Success, snapshot);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            return new(null, RoomLoadStatus.RetryableError);
        }
    }

    public async Task<RoomRewardsResponse?> GetRoomRewardsAsync(int roomId, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, $"api/rooms/{roomId}/rewards", requiresAuth: true, scope: ApiRequestScope.Room);
        using var response = await SendTrackedAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        var result = await response.Content.ReadFromJsonAsync<RoomRewardsResponse>(cancellationToken);
        return IsRequestContextCurrent(request) && result?.RoomId == roomId ? result : null;
    }
}
