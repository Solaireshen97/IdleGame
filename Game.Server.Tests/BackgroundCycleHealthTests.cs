using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Game.Server.Tests;

public sealed class BackgroundCycleHealthTests
{
    [Fact]
    public void EmptyScansKeepWorkerHealthyAndAStalledWorkerBecomesUnhealthy()
    {
        var clock = new ManualClock();
        var health = new BackgroundCycleHealth(clock);
        health.Register(BackgroundCycleHealth.Rooms);
        Assert.Equal("Starting", Assert.Single(health.Snapshot()).Status);

        for (var i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            health.CompleteScan(BackgroundCycleHealth.Rooms, 0, 0);
            Assert.Equal("Healthy", Assert.Single(health.Snapshot()).Status);
        }

        clock.Advance(BackgroundCycleHealth.MaximumScanAge + TimeSpan.FromSeconds(1));
        var stale = Assert.Single(health.Snapshot());
        Assert.Equal("Unhealthy", stale.Status);
        Assert.Equal(0, stale.ItemsFound);
        Assert.Equal(0, stale.ConsecutiveFailedScans);
    }

    [Fact]
    public void ItemAndQueryFailuresPreserveLastSuccessAndRecoverAfterAFullSuccess()
    {
        var clock = new ManualClock();
        var health = new BackgroundCycleHealth(clock);
        health.CompleteScan(BackgroundCycleHealth.Production, 4, 0);
        var lastSuccess = Assert.Single(health.Snapshot()).LastSuccessfulScanAtUtc;

        clock.Advance(TimeSpan.FromSeconds(2));
        health.CompleteScan(BackgroundCycleHealth.Production, 4, 1);
        var partial = Assert.Single(health.Snapshot());
        Assert.Equal("Degraded", partial.Status);
        Assert.Equal(1, partial.FailedItems);
        Assert.Equal(lastSuccess, partial.LastSuccessfulScanAtUtc);
        Assert.Equal(clock.GetUtcNow(), partial.LastScanCompletedAtUtc);

        health.FailScan(BackgroundCycleHealth.Production);
        health.FailScan(BackgroundCycleHealth.Production);
        var failed = Assert.Single(health.Snapshot());
        Assert.Equal("Unhealthy", failed.Status);
        Assert.Equal(3, failed.ConsecutiveFailedScans);
        Assert.Null(failed.ItemsFound);
        Assert.Null(failed.FailedItems);

        clock.Advance(TimeSpan.FromSeconds(2));
        health.CompleteScan(BackgroundCycleHealth.Production, 0, 0);
        var recovered = Assert.Single(health.Snapshot());
        Assert.Equal("Healthy", recovered.Status);
        Assert.Equal(0, recovered.ConsecutiveFailedScans);
        Assert.Equal(clock.GetUtcNow(), recovered.LastSuccessfulScanAtUtc);
    }

    [Fact]
    public void NeverCompletedInitialScanBecomesUnhealthyAfterStartupGrace()
    {
        var clock = new ManualClock();
        var health = new BackgroundCycleHealth(clock);
        health.Register(BackgroundCycleHealth.Rooms);
        clock.Advance(BackgroundCycleHealth.MaximumScanAge + TimeSpan.FromSeconds(1));
        Assert.Equal("Unhealthy", Assert.Single(health.Snapshot()).Status);
    }

    [Fact]
    public void MovingUtcClockBackwardsDoesNotHideAStalledWorker()
    {
        var clock = new ManualClock();
        var health = new BackgroundCycleHealth(clock);
        health.CompleteScan(BackgroundCycleHealth.Rooms, 0, 0);
        clock.Advance(BackgroundCycleHealth.MaximumScanAge + TimeSpan.FromSeconds(1));
        clock.MoveUtc(TimeSpan.FromHours(-1));
        var snapshot = Assert.Single(health.Snapshot());
        Assert.Equal("Unhealthy", snapshot.Status);
        Assert.Equal(31, snapshot.SecondsSinceSuccessfulScanOrStartup);
    }

    [Fact]
    public async Task ProductionWorkerReportsAnItemFailureInsteadOfACompleteSuccess()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<GameDbContext>(options => options.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.ProductionTasks.Add(new ProductionTask
            {
                UserId = 1, CharacterId = 1, NextCycleAtUtc = DateTime.UtcNow.AddMinutes(-1),
                EndsAtUtc = DateTime.UtcNow.AddMinutes(10), StartedAtUtc = DateTime.UtcNow.AddMinutes(-2),
                CycleSeconds = 60, OutputQuantity = 1
            });
            await db.SaveChangesAsync();
        }

        // Deliberately omit ProductionService so advancing this due item fails inside the item loop.
        var health = new BackgroundCycleHealth();
        using var worker = new ProductionCycleService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProductionCycleService>.Instance, health);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForScanAsync(health);
            var snapshot = Assert.Single(health.Snapshot());
            Assert.Equal("Degraded", snapshot.Status);
            Assert.Equal(1, snapshot.ItemsFound);
            Assert.Equal(1, snapshot.FailedItems);
            Assert.Null(snapshot.LastSuccessfulScanAtUtc);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task RoomWorkerQueryFailureIsReportedAndStoppingDoesNotAddAnotherFailure()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var health = new BackgroundCycleHealth();
        using var worker = new RoomCycleService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RoomCycleService>.Instance, health);
        await worker.StartAsync(CancellationToken.None);
        await WaitForScanAsync(health);
        await worker.StopAsync(CancellationToken.None);
        var snapshot = Assert.Single(health.Snapshot());
        Assert.Equal(1, snapshot.ConsecutiveFailedScans);
        Assert.Null(snapshot.ItemsFound);
        Assert.Null(snapshot.LastSuccessfulScanAtUtc);
    }

    [Fact]
    public async Task CancellationBeforeTheFirstScanDoesNotRecordAFailure()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var health = new BackgroundCycleHealth();
        using var worker = new ProductionCycleService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProductionCycleService>.Instance, health);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await worker.StartAsync(cancellation.Token);
        await worker.StopAsync(CancellationToken.None);
        // .NET 10 schedules all of ExecuteAsync in the background. A token cancelled before
        // StartAsync prevents it from running, so neither registration nor a scan is observed.
        Assert.Empty(health.Snapshot());
    }

    private static async Task WaitForScanAsync(BackgroundCycleHealth health)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (health.Snapshot().SingleOrDefault()?.LastScanCompletedAtUtc is null)
            await Task.Delay(10, deadline.Token);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => now;
        public void MoveUtc(TimeSpan duration) => now += duration;
        public void Advance(TimeSpan duration)
        {
            now += duration;
            timestamp += duration.Ticks;
        }
    }
}
