using Game.Shared.Dtos;

namespace Game.Server.Services;

public sealed class BattleLogStore
{
    private const int MaximumEntriesPerRoom = 80;
    private readonly object _gate = new();
    private readonly Dictionary<int, List<BattleLogResponse>> _entriesByRoom = [];
    private long _nextId;

    public void Append(int roomId, IEnumerable<string> logs, DateTime createdAtUtc)
    {
        var messages = logs.Where(message => !string.IsNullOrWhiteSpace(message)).ToList();
        if (messages.Count == 0) return;
        lock (_gate)
        {
            if (!_entriesByRoom.TryGetValue(roomId, out var entries))
            {
                entries = [];
                _entriesByRoom[roomId] = entries;
            }
            foreach (var message in messages)
                entries.Add(new BattleLogResponse { Id = ++_nextId, Text = message, CreatedAtUtc = createdAtUtc });
            if (entries.Count > MaximumEntriesPerRoom)
                entries.RemoveRange(0, entries.Count - MaximumEntriesPerRoom);
        }
    }

    public List<BattleLogResponse> Get(int roomId)
    {
        lock (_gate)
        {
            return _entriesByRoom.TryGetValue(roomId, out var entries)
                ? entries.Select(entry => new BattleLogResponse
                {
                    Id = entry.Id,
                    Text = entry.Text,
                    CreatedAtUtc = entry.CreatedAtUtc
                }).ToList()
                : [];
        }
    }

    public void Clear(int roomId)
    {
        lock (_gate) _entriesByRoom.Remove(roomId);
    }

    public void Replace(int roomId, IEnumerable<string> logs, DateTime createdAtUtc)
    {
        var messages = logs.Where(message => !string.IsNullOrWhiteSpace(message)).ToList();
        lock (_gate)
        {
            _entriesByRoom.Remove(roomId);
            if (messages.Count == 0) return;
            _entriesByRoom[roomId] = messages.TakeLast(MaximumEntriesPerRoom)
                .Select(message => new BattleLogResponse
                {
                    Id = ++_nextId, Text = message, CreatedAtUtc = createdAtUtc
                }).ToList();
        }
    }
}
