using Game.Shared.Dtos;

namespace Game.Server.Services;

public sealed record BattleHistorySnapshot(List<BattleLogResponse> Logs, List<BattleEventResponse> Events, string Epoch = "");

public sealed class BattleLogStore
{
    private const int MaximumEntriesPerRoom = 80;
    private readonly object _gate = new();
    private readonly Dictionary<int, List<BattleLogResponse>> _entriesByRoom = [];
    private readonly Dictionary<int, List<BattleEventResponse>> _eventsByRoom = [];
    private long _nextId;
    private long _nextEventId;
    private readonly Dictionary<int, DateTimeOffset> _lastPublished = [];
    private readonly Dictionary<int, long> _publicationOrder = [];
    private long _nextPublication;
    private readonly TimeProvider _clock;
    private readonly int _maximumRooms;
    private readonly TimeSpan _timeToLive;
    public string Epoch { get; } = Guid.NewGuid().ToString("N");

    public BattleLogStore(TimeProvider? clock = null, int maximumRooms = 256, TimeSpan? timeToLive = null)
    {
        if (maximumRooms < 1) throw new ArgumentOutOfRangeException(nameof(maximumRooms));
        _clock = clock ?? TimeProvider.System;
        _maximumRooms = maximumRooms;
        _timeToLive = timeToLive ?? TimeSpan.FromHours(2);
        if (_timeToLive <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeToLive));
    }

    public List<BattleEventResponse> Append(int roomId, IEnumerable<string> logs, DateTime createdAtUtc,
        IEnumerable<BattleEventResponse>? events = null) => Publish(roomId, logs, createdAtUtc, events, false);

    public List<BattleEventResponse> Replace(int roomId, IEnumerable<string> logs, DateTime createdAtUtc,
        IEnumerable<BattleEventResponse>? events = null) => Publish(roomId, logs, createdAtUtc, events, true);

    private List<BattleEventResponse> Publish(int roomId, IEnumerable<string> logs, DateTime createdAtUtc,
        IEnumerable<BattleEventResponse>? events, bool replace)
    {
        var messages = logs.Where(message => !string.IsNullOrWhiteSpace(message)).ToList();
        var facts = events?.ToList() ?? [];
        lock (_gate)
        {
            Prune();
            if (replace) { _entriesByRoom.Remove(roomId); _eventsByRoom.Remove(roomId); }
            if (messages.Count > 0)
            {
                if (!_entriesByRoom.TryGetValue(roomId, out var entries))
                    _entriesByRoom[roomId] = entries = [];
                entries.AddRange(messages.Select(message => new BattleLogResponse
                    { Id = ++_nextId, Text = message, CreatedAtUtc = createdAtUtc }));
                Trim(entries, messages.Count);
            }
            if (facts.Count > 0)
            {
                if (!_eventsByRoom.TryGetValue(roomId, out var entries))
                    _eventsByRoom[roomId] = entries = [];
                facts = facts.Select(fact => fact with { Id = ++_nextEventId }).ToList();
                entries.AddRange(facts);
                Trim(entries, facts.Count);
            }
            if (messages.Count > 0 || facts.Count > 0)
            {
                _lastPublished[roomId] = _clock.GetUtcNow();
                _publicationOrder[roomId] = ++_nextPublication;
            }
            while (_lastPublished.Count > _maximumRooms)
                RemoveRoom(_publicationOrder.MinBy(entry => entry.Value).Key);
            return facts;
        }
    }

    // Preserve the entire newest settlement, including long chains and transient states.
    private static void Trim<T>(List<T> entries, int settlementSize)
    {
        var retainedCount = Math.Max(MaximumEntriesPerRoom, settlementSize);
        if (entries.Count > retainedCount) entries.RemoveRange(0, entries.Count - retainedCount);
    }

    public BattleHistorySnapshot GetSnapshot(int roomId)
    {
        lock (_gate)
        {
            Prune();
            return new(_entriesByRoom.TryGetValue(roomId, out var logs)
                ? logs.Select(entry => new BattleLogResponse
                    { Id = entry.Id, Text = entry.Text, CreatedAtUtc = entry.CreatedAtUtc }).ToList() : [],
                _eventsByRoom.TryGetValue(roomId, out var events) ? events.ToList() : [], Epoch);
        }
    }

    public List<BattleLogResponse> Get(int roomId) => GetSnapshot(roomId).Logs;
    public List<BattleEventResponse> GetEvents(int roomId) => GetSnapshot(roomId).Events;

    public void Clear(int roomId)
    {
        lock (_gate) { RemoveRoom(roomId); }
    }

    private void Prune()
    {
        var cutoff = _clock.GetUtcNow() - _timeToLive;
        foreach (var room in _lastPublished.Where(entry => entry.Value <= cutoff).Select(entry => entry.Key).ToList())
            RemoveRoom(room);
    }

    private void RemoveRoom(int roomId)
    {
        _entriesByRoom.Remove(roomId);
        _eventsByRoom.Remove(roomId);
        _lastPublished.Remove(roomId);
        _publicationOrder.Remove(roomId);
    }
}
