namespace Game.Client.Services;

public partial class ApiService
{
    // Keep an uncertain command's identity across retries and page navigation.
    // A parsed successful response completes it; the next click is a new command.
    private readonly Dictionary<string, string> _pendingEconomicRequests = new();

    private async Task<(string Key, string Id)> BeginEconomicRequestAsync(string command, string? suppliedId)
    {
        await EnsureContextAsync();
        var key = $"{SessionRevision}:{command}";
        if (suppliedId is not null) return (key, suppliedId);
        if (!_pendingEconomicRequests.TryGetValue(key, out var id))
            _pendingEconomicRequests[key] = id = Guid.NewGuid().ToString("N");
        return (key, id);
    }

    private void CompleteEconomicRequest((string Key, string Id) request)
    {
        if (_pendingEconomicRequests.TryGetValue(request.Key, out var id) && id == request.Id)
            _pendingEconomicRequests.Remove(request.Key);
    }
}
