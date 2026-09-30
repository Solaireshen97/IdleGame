namespace Game.Server.Services;

/// <summary>Process-local observations; no exception messages or user/task identifiers are exposed.</summary>
public sealed class BackgroundCycleHealth(TimeProvider? timeProvider = null)
{
    public const string Rooms = "rooms";
    public const string Production = "production";
    public static readonly TimeSpan MaximumScanAge = TimeSpan.FromSeconds(30);
    public const int MaximumConsecutiveFailures = 3;

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, CycleState> cycles = new();

    public void Register(string name)
    {
        lock (gate)
            cycles.TryAdd(name, new CycleState(clock.GetTimestamp()));
    }

    public void CompleteScan(string name, int itemsFound, int failedItems)
    {
        lock (gate)
        {
            var state = GetState(name);
            state.LastCompleted = clock.GetUtcNow();
            state.ItemsFound = itemsFound;
            state.FailedItems = failedItems;
            if (failedItems == 0)
            {
                state.LastSuccessful = state.LastCompleted;
                state.LastSuccessfulTimestamp = clock.GetTimestamp();
                state.ConsecutiveFailures = 0;
            }
            else
            {
                state.ConsecutiveFailures++;
            }
        }
    }

    public void FailScan(string name)
    {
        lock (gate)
        {
            var state = GetState(name);
            state.LastCompleted = clock.GetUtcNow();
            state.ConsecutiveFailures++;
            // The query did not finish, so there is no reliable per-item count for this scan.
            state.ItemsFound = null;
            state.FailedItems = null;
        }
    }

    public IReadOnlyList<BackgroundCycleSnapshot> Snapshot()
    {
        lock (gate)
        {
            var now = clock.GetTimestamp();
            return cycles.OrderBy(pair => pair.Key).Select(pair =>
            {
                var state = pair.Value;
                var age = clock.GetElapsedTime(state.LastSuccessfulTimestamp ?? state.StartedTimestamp, now);
                var status = age > MaximumScanAge || state.ConsecutiveFailures >= MaximumConsecutiveFailures
                    ? "Unhealthy"
                    : state.ConsecutiveFailures > 0 ? "Degraded"
                    : state.LastSuccessful is null ? "Starting" : "Healthy";
                return new BackgroundCycleSnapshot(pair.Key, status, state.LastCompleted,
                    state.LastSuccessful, state.ConsecutiveFailures, state.ItemsFound, state.FailedItems,
                    Math.Max(0, age.TotalSeconds));
            }).ToArray();
        }
    }

    private CycleState GetState(string name)
    {
        if (!cycles.TryGetValue(name, out var state))
            cycles.Add(name, state = new CycleState(clock.GetTimestamp()));
        return state;
    }

    private sealed class CycleState(long startedTimestamp)
    {
        public long StartedTimestamp { get; } = startedTimestamp;
        public long? LastSuccessfulTimestamp { get; set; }
        public DateTimeOffset? LastCompleted { get; set; }
        public DateTimeOffset? LastSuccessful { get; set; }
        public int ConsecutiveFailures { get; set; }
        public int? ItemsFound { get; set; }
        public int? FailedItems { get; set; }
    }
}

public sealed record BackgroundCycleSnapshot(string Name, string Status,
    DateTimeOffset? LastScanCompletedAtUtc, DateTimeOffset? LastSuccessfulScanAtUtc,
    int ConsecutiveFailedScans, int? ItemsFound, int? FailedItems,
    double SecondsSinceSuccessfulScanOrStartup);
