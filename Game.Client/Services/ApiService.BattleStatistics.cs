using System.Net.Http.Json;
using Game.Shared.Dtos;

namespace Game.Client.Services;

public partial class ApiService
{
    public async Task<BattleStatisticsResponse?> GetBattleStatisticsAsync(int roomId, string scope,
        int? runSequence = null, int? monsterId = null, int? characterId = null,
        CancellationToken cancellationToken = default)
    {
        var query = $"scope={Uri.EscapeDataString(scope)}";
        if (runSequence.HasValue) query += $"&runSequence={runSequence.Value}";
        if (monsterId.HasValue) query += $"&monsterId={monsterId.Value}";
        if (characterId.HasValue) query += $"&characterId={characterId.Value}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = await CreateRequestAsync(HttpMethod.Get,
            $"api/rooms/{roomId}/statistics?{query}", requiresAuth: true, scope: ApiRequestScope.Room);
        using var response = await SendTrackedAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode) return null;
        var result = await response.Content.ReadFromJsonAsync<BattleStatisticsResponse>(timeout.Token);
        return IsRequestContextCurrent(request) && result?.RoomId == roomId ? result : null;
    }
}
