using Game.Shared.Dtos.Inventory;

namespace Game.Client.Services;

// One state per warehouse view. Browsing never changes the account selection.
public sealed class InventoryState : IDisposable
{
    private readonly ApiService _api;
    private long _generation;
    private long _session;
    private bool _disposed;
    private int? _characterId;
    public InventoryOverviewResponse? Overview { get; private set; }
    public string? LoadedCategory { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool IsLoading { get; private set; }
    public bool IsStale { get; private set; }
    public event Action? Changed;

    public InventoryState(ApiService api)
    {
        _api = api;
        _session = api.SessionRevision;
        api.ContextChanged += ObserveContext;
        api.InventoryChanged += Invalidate;
    }

    public async Task LoadAsync(int characterId, InventoryQueryRequest query, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (_disposed) return;
        await _api.EnsureContextAsync();
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<InventoryQueryRequest>(System.Text.Json.JsonSerializer.Serialize(query))!;
        if (_characterId != characterId)
        {
            Overview = null;
            LoadedCategory = null;
            _characterId = characterId;
        }
        var generation = ++_generation;
        var session = _api.SessionRevision;
        IsLoading = true;
        ErrorMessage = null;
        Changed?.Invoke();
        try
        {
            var result = await _api.GetInventoryAsync(characterId, snapshot, forceRefresh, ct);
            if (_disposed || generation != _generation || session != _api.SessionRevision) return;
            if (result.Response is { } current && current.CharacterId == characterId)
            {
                Overview = current;
                LoadedCategory = snapshot.Category;
                IsStale = false;
            }
            else
            {
                ErrorMessage = result.ErrorMessage ?? "库存暂时无法读取，已保留上次同步数据。";
                IsStale = true;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (generation == _generation) IsStale = true;
        }
        finally
        {
            if (!_disposed && generation == _generation)
            {
                IsLoading = false;
                Changed?.Invoke();
            }
        }
    }

    public void Invalidate(int characterId)
    {
        if (_characterId != characterId) return;
        _generation++;
        IsLoading = false;
        IsStale = true;
        ErrorMessage = null;
        Changed?.Invoke();
    }

    private void ObserveContext()
    {
        if (_session == _api.SessionRevision) return;
        _session = _api.SessionRevision;
        _generation++;
        _characterId = null;
        Overview = null;
        LoadedCategory = null;
        ErrorMessage = null;
        IsLoading = false;
        IsStale = true;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        _generation++;
        _api.ContextChanged -= ObserveContext;
        _api.InventoryChanged -= Invalidate;
    }
}
