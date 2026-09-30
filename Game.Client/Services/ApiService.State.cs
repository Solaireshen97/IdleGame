using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Game.Shared.Dtos.Characters;

namespace Game.Client.Services;

public partial class ApiService : IDisposable
{
    private readonly HttpClient httpClient;
    private readonly UserSessionService userSessionService;
    private readonly ClientQueryCache _queries = new();
    private static readonly TimeSpan SummaryLifetime = TimeSpan.FromSeconds(10);
    private static readonly HttpRequestOptionsKey<long> ContextRevisionKey = new("GameContextRevision");
    public long DataRevision { get; private set; }
    public event Action? ContextChanged;

    public ApiService(HttpClient httpClient, UserSessionService userSessionService)
    {
        this.httpClient = httpClient;
        this.userSessionService = userSessionService;
        userSessionService.Changed += ChangeContext;
    }

    private void ChangeContext()
    {
        DataRevision++;
        _queries.Invalidate();
        ContextChanged?.Invoke();
    }

    public void InvalidateCharacter() => _queries.Invalidate("character");
    public async Task EnsureContextAsync() => await userSessionService.GetToken();
    public void ObserveCharacterChange(CurrentCharacterResponse previous, CurrentCharacterResponse current)
    {
        if (previous.CharacterId != current.CharacterId)
        {
            ChangeContext();
            ApplyCharacterSummary(current);
        }
        else if (previous.Level != current.Level || previous.ProfessionCode != current.ProfessionCode)
        {
            _queries.Invalidate($"skills:{current.CharacterId}");
            _queries.Invalidate($"professions:{current.CharacterId}");
            _queries.Invalidate("dungeon-progress");
        }
    }

    public void ApplyCharacterSummary(CurrentCharacterResponse value) =>
        _queries.Set("character", Clone(value), SummaryLifetime);

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value))!;

    private async Task<T?> ReadCachedAsync<T>(string key, Func<Task<T?>> read, bool force = false) where T : class
    {
        // Observe changes from other browser tabs before selecting the cache scope.
        await userSessionService.GetToken();
        var revision = DataRevision;
        for (var attempt = 0; attempt < 3 && revision == DataRevision; attempt++)
        {
            try
            {
                var value = await _queries.GetAsync(key, read, SummaryLifetime, force && attempt == 0);
                return value is null ? null : Clone(value);
            }
            catch (ClientQueryCache.InvalidatedException) { }
        }
        return null;
    }

    private async Task<HttpResponseMessage> SendTrackedAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        var hasContext = request.Options.TryGetValue(ContextRevisionKey, out var revision);
        var mutation = request.Method != HttpMethod.Get && request.Method != HttpMethod.Head;
        var path = request.RequestUri?.OriginalString ?? "";
        // Polling is authoritative state synchronization, not a cache-invalidating user command.
        var invalidates = mutation && path is not ("api/battle/sync" or "api/battle/snapshot");
        if (invalidates) _queries.Invalidate();
        var response = await httpClient.SendAsync(request, cancellationToken);
        if (hasContext) await userSessionService.GetToken();
        if (hasContext && revision != DataRevision)
        {
            response.Dispose();
            return new(HttpStatusCode.Conflict)
            {
                RequestMessage = request,
                Content = new StringContent("ActiveCharacterChanged")
            };
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            await userSessionService.ClearTokenIfCurrent(request.Headers.Authorization?.Parameter);
        else if (invalidates && response.IsSuccessStatusCode)
        {
            _queries.Invalidate();
            if (path == "api/user/character/select" || path == "api/user/characters" ||
                request.Method == HttpMethod.Delete && path.StartsWith("api/user/characters/", StringComparison.Ordinal))
                ChangeContext();
        }
        return response;
    }

    private ValueTask ClearResponseSessionAsync(HttpResponseMessage response) =>
        userSessionService.ClearTokenIfCurrent(response.RequestMessage?.Headers.Authorization?.Parameter);

    private async Task<T?> ReadContextResponseAsync<T>(HttpResponseMessage response) where T : class
    {
        var result = await response.Content.ReadFromJsonAsync<T>();
        return response.RequestMessage?.Options.TryGetValue(ContextRevisionKey, out var revision) == true &&
            revision != DataRevision ? null : result;
    }

    private async Task<(T? Response, string? ErrorMessage)> ReadCachedResultAsync<T>(string key,
        Func<Task<(T? Response, string? ErrorMessage)>> read) where T : class
    {
        string? error = null;
        var result = await ReadCachedAsync(key, async () =>
        {
            var response = await read();
            error = response.ErrorMessage;
            return response.Response;
        });
        return (result, result is null ? error ?? "数据暂时无法读取，请重试。" : null);
    }

    private async Task<(T? Response, string? ErrorMessage)> ReadCharacterResultAsync<T>(
        HttpResponseMessage response, HttpMethod method, string resource, Func<T, int> characterId) where T : class
    {
        var value = await ReadContextResponseAsync<T>(response);
        if (value is not null && method != HttpMethod.Get)
            _queries.Set($"{resource}:{characterId(value)}", Clone(value), SummaryLifetime);
        return (value, value is null ? "当前角色已变化，请刷新。" : null);
    }

    public void Dispose() => userSessionService.Changed -= ChangeContext;
}
