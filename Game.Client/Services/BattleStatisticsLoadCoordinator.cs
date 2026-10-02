using Game.Shared.Dtos;

namespace Game.Client.Services;

// A request identity includes its resolved run, even for the following-current view.
public sealed record BattleStatisticsRequestKey(int RoomId, long SessionRevision, string Scope,
    int? RunSequence, int? MonsterId, int? CharacterId);

public sealed class BattleStatisticsLoadCoordinator : IDisposable
{
    private CancellationTokenSource? _cancellation;
    private long _generation;
    private readonly Dictionary<BattleStatisticsRequestKey, int> _versions = [];
    public BattleStatisticsRequestKey? Current { get; private set; }

    public (long Generation, CancellationToken Token) Begin(BattleStatisticsRequestKey key)
    {
        Cancel();
        Current = key;
        _cancellation = new();
        return (_generation, _cancellation.Token);
    }

    public bool IsCurrent(BattleStatisticsRequestKey key, long generation) =>
        Current == key && generation == _generation && _cancellation?.IsCancellationRequested == false;

    public bool Accept(BattleStatisticsRequestKey key, long generation, BattleStatisticsResponse value)
    {
        if (!IsCurrent(key, generation) || value.RoomId != key.RoomId ||
            value.Scope != key.Scope || value.MonsterId != key.MonsterId || value.CharacterId != key.CharacterId ||
            key.Scope == "run" && value.RunSequence != key.RunSequence ||
            key.Scope == "current" && (!value.RunSequence.HasValue || value.RunSequence < key.RunSequence)) return false;
        if (_versions.TryGetValue(key, out var version) && value.RoomVersion < version) return false;
        _versions[key] = value.RoomVersion;
        return true;
    }

    public void Cancel()
    {
        _generation++;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        Current = null;
    }

    public void Dispose() => Cancel();
}
