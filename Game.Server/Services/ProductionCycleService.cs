using Game.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class ProductionCycleService(IServiceScopeFactory scopes, ILogger<ProductionCycleService> logger,
    BackgroundCycleHealth? health = null, TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        health?.Register(BackgroundCycleHealth.Production);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), clock);
        try
        {
            do
            {
                try { await AdvanceTasksAsync(stoppingToken); }
                catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    health?.FailScan(BackgroundCycleHealth.Production);
                    logger.LogError(exception, "Failed to scan production tasks.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task AdvanceTasksAsync(CancellationToken cancellationToken)
    {
        using var listScope = scopes.CreateScope();
        var db = listScope.ServiceProvider.GetRequiredService<GameDbContext>();
        var now = clock.GetUtcNow().UtcDateTime;
        var ids = await db.ProductionTasks.AsNoTracking()
            .Where(task => task.Status == "Running" &&
                (task.NextCycleAtUtc <= now || task.EndsAtUtc <= now))
            .Select(task => task.Id).ToListAsync(cancellationToken);
        var failedItems = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var scope = scopes.CreateScope();
                var production = scope.ServiceProvider.GetRequiredService<ProductionService>();
                var error = await production.AdvanceDueAsync(id, clock.GetUtcNow().UtcDateTime);
                if (error is not null && error != "ConcurrencyConflict")
                {
                    failedItems++;
                    logger.LogWarning("Production task {TaskId} failed: {Error}", id, error);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                failedItems++;
                logger.LogError(exception, "Production task {TaskId} failed.", id);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        health?.CompleteScan(BackgroundCycleHealth.Production, ids.Count, failedItems);
    }
}
