namespace Game.Client.Services;

// Values and pending requests have the same lifetime. Invalidating a key also
// prevents an older request from repopulating it after a successful mutation.
public sealed class ClientQueryCache(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, object> _entries = [];
    private readonly object _gate = new();

    public Task<T?> GetAsync<T>(string key, Func<Task<T?>> read, TimeSpan lifetime, bool force = false) where T : class
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing) && existing is Entry<T> entry &&
                (!entry.Completion.Task.IsCompleted || !force && entry.ExpiresAt > _clock.GetUtcNow()))
                return entry.Completion.Task;
            var next = new Entry<T>();
            _entries[key] = next;
            _ = LoadAsync(key, next, read, lifetime);
            return next.Completion.Task;
        }
    }

    public void Set<T>(string key, T value, TimeSpan lifetime) where T : class
    {
        lock (_gate)
        {
            var entry = new Entry<T> { ExpiresAt = _clock.GetUtcNow() + lifetime };
            entry.Completion.SetResult(value);
            _entries[key] = entry;
        }
    }

    public void Invalidate(string? key = null)
    {
        lock (_gate)
        {
            if (key is null) _entries.Clear();
            else _entries.Remove(key);
        }
    }

    private async Task LoadAsync<T>(string key, Entry<T> entry, Func<Task<T?>> read, TimeSpan lifetime) where T : class
    {
        try
        {
            var value = await read();
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out var current) || !ReferenceEquals(current, entry))
                    throw new InvalidatedException();
                else if (value is null) _entries.Remove(key);
                else entry.ExpiresAt = _clock.GetUtcNow() + lifetime;
            }
            entry.Completion.TrySetResult(value);
        }
        catch (Exception exception)
        {
            lock (_gate)
                if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry)) _entries.Remove(key);
            entry.Completion.TrySetException(exception);
        }
    }

    private sealed class Entry<T> where T : class
    {
        public TaskCompletionSource<T?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset ExpiresAt { get; set; }
    }

    public sealed class InvalidatedException : Exception;
}
