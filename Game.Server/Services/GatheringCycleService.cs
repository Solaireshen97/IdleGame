using Game.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class GatheringCycleService(IServiceScopeFactory scopes, ILogger<GatheringCycleService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            do
            {
                try { await AdvanceTasksAsync(stoppingToken); }
                catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(exception, "Failed to scan gathering tasks.");
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
        var now = DateTime.UtcNow;
        var ids = await db.GatheringTasks.AsNoTracking()
            .Where(task => task.Status == "Running" &&
                (task.NextCycleAtUtc <= now || task.EndsAtUtc <= now))
            .Select(task => task.Id).ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var scope = scopes.CreateScope();
                var gathering = scope.ServiceProvider.GetRequiredService<GatheringService>();
                var error = await gathering.AdvanceDueAsync(id, DateTime.UtcNow);
                if (error is not null && error != "ConcurrencyConflict")
                    logger.LogWarning("Gathering task {TaskId} failed: {Error}", id, error);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Gathering task {TaskId} failed.", id);
            }
        }
    }
}
