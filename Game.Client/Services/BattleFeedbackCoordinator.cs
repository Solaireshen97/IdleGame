using Game.Shared.Dtos;

namespace Game.Client.Services;

// Keeps the pre-settlement scene until its committed facts arrive, independently of polling IDs.
public sealed class BattleFeedbackCoordinator
{
    private RoomDetailResponse? _pendingScene;
    private (int Room, int Run, int Round)? _played;
    private int _closedRetries;
    public bool NeedsClosedRetry => _pendingScene is not null && _closedRetries > 0;
    public bool BeginClosedRetry()
    {
        if (!NeedsClosedRetry) return false;
        _closedRetries--;
        return true;
    }

    public static bool IsStale(RoomDetailResponse? current, RoomDetailResponse? incoming) =>
        current is not null && incoming is not null && current.RoomId == incoming.RoomId &&
        incoming.RoomVersion < current.RoomVersion;

    public (RoomDetailResponse Scene, BattleFeedbackPlan Plan)? Observe(
        RoomDetailResponse? previous, RoomDetailResponse? latest, bool enabled)
    {
        if (!enabled || latest is null)
        {
            CancelPending();
            return null;
        }
        if (IsStale(previous, latest)) return null;
        if (_pendingScene is { } pending && (pending.RoomId != latest.RoomId ||
            pending.RunSequence != latest.RunSequence || latest.RoundNumber != pending.RoundNumber + 1))
            CancelPending();
        if (_pendingScene is null && previous is { ClosedAtUtc: null, MonsterHp: > 0 } &&
            previous.RoomId == latest.RoomId && previous.RunSequence == latest.RunSequence &&
            latest.RoundNumber == previous.RoundNumber + 1 &&
            _played != (latest.RoomId, latest.RunSequence, latest.RoundNumber))
        {
            _pendingScene = previous;
            _closedRetries = 3;
        }
        if (_pendingScene is not { } scene) return null;
        if (BattleFeedbackPlanner.Create(scene, latest) is { } plan)
        {
            _played = (latest.RoomId, latest.RunSequence, latest.RoundNumber);
            CancelPending();
            return (scene, plan);
        }
        if (latest.ClosedAtUtc.HasValue && _closedRetries <= 0) CancelPending();
        return null;
    }

    public void CancelPending()
    {
        _pendingScene = null;
        _closedRetries = 0;
    }
}

// A process epoch prevents restarted log IDs colliding; a high water mark bounds client memory.
public sealed class BattleLogCursor
{
    private string? _epoch;
    private int _roomId;
    private long _lastId;
    public bool Accept(string epoch, long id, int roomId = 0)
    {
        if (_epoch != epoch || _roomId != roomId) { _epoch = epoch; _roomId = roomId; _lastId = 0; }
        if (id <= _lastId) return false;
        _lastId = id;
        return true;
    }
    public void Reset() { _epoch = null; _roomId = 0; _lastId = 0; }
}
