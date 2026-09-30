using Game.Client.Services;
using Xunit;

namespace Game.Server.Tests;

public sealed class ClientQueryCacheTests
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ConcurrentNormalAndForcedReadsShareOnePendingRequest()
    {
        var cache = new ClientQueryCache();
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<string?> Read() { calls++; return pending.Task; }

        var readers = Enumerable.Range(0, 20)
            .Select(index => cache.GetAsync("summary", Read, Lifetime, force: index % 2 == 0)).ToArray();

        Assert.Equal(1, calls);
        Assert.All(readers, task => Assert.Same(readers[0], task));
        pending.SetResult("fresh");
        Assert.All(await Task.WhenAll(readers), value => Assert.Equal("fresh", value));
        Assert.Equal("fresh", await cache.GetAsync("summary", Read, Lifetime));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LifetimeStartsAtCompletionAndExpiryOrForceRefreshes()
    {
        var clock = new ManualClock();
        var cache = new ClientQueryCache(clock);
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = cache.GetAsync("summary", () => pending.Task, Lifetime);
        clock.Advance(TimeSpan.FromMinutes(1));
        pending.SetResult("initial");
        Assert.Equal("initial", await first);

        var calls = 0;
        Task<string?> Read() => Task.FromResult<string?>($"value-{++calls}");
        clock.Advance(Lifetime - TimeSpan.FromTicks(1));
        Assert.Equal("initial", await cache.GetAsync("summary", Read, Lifetime));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal("value-1", await cache.GetAsync("summary", Read, Lifetime));
        Assert.Equal("value-2", await cache.GetAsync("summary", Read, Lifetime, force: true));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidatingPendingReadRejectsOldCompletionAndPreservesReplacement(bool allKeys)
    {
        var cache = new ClientQueryCache();
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = cache.GetAsync("summary", () => pending.Task, Lifetime);
        cache.Invalidate(allKeys ? null : "summary");
        var replacement = await cache.GetAsync("summary", () => Task.FromResult<string?>("new"), Lifetime);
        pending.SetResult("old");

        await Assert.ThrowsAsync<ClientQueryCache.InvalidatedException>(async () => await stale);
        Assert.Equal("new", replacement);
        Assert.Equal("new", await cache.GetAsync<string>("summary",
            () => throw new InvalidOperationException("Replacement must remain cached."), Lifetime));
    }

    [Fact]
    public async Task ApplyingAuthoritativeValueRejectsAnOlderPendingRead()
    {
        var cache = new ClientQueryCache();
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = cache.GetAsync("summary", () => pending.Task, Lifetime);
        cache.Set("summary", "authoritative", Lifetime);
        pending.SetResult("old");

        await Assert.ThrowsAsync<ClientQueryCache.InvalidatedException>(async () => await stale);
        Assert.Equal("authoritative", await cache.GetAsync("summary",
            () => Task.FromResult<string?>("unexpected"), Lifetime));
    }

    [Fact]
    public async Task FailedAndNullResponsesDoNotPoisonFutureReads()
    {
        var cache = new ClientQueryCache();
        var failure = cache.GetAsync<string>("summary", () => Task.FromException<string?>(new HttpRequestException()), Lifetime);
        await Assert.ThrowsAsync<HttpRequestException>(async () => await failure);
        Assert.Null(await cache.GetAsync<string>("summary", () => Task.FromResult<string?>(null), Lifetime));
        Assert.Equal("recovered", await cache.GetAsync("summary", () => Task.FromResult<string?>("recovered"), Lifetime));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
