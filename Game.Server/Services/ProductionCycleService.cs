using Game.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class ProductionCycleService(IServiceScopeFactory scopes, ILogger<ProductionCycleService> logger) : BackgroundService
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
        var now = DateTime.UtcNow;
        var ids = await db.ProductionTasks.AsNoTracking()
            .Where(task => task.Status == "Running" &&
                (task.NextCycleAtUtc <= now || task.EndsAtUtc <= now))
            .Select(task => task.Id).ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var scope = scopes.CreateScope();
                var production = scope.ServiceProvider.GetRequiredService<ProductionService>();
                var error = await production.AdvanceDueAsync(id, DateTime.UtcNow);
                if (error is not null && error != "ConcurrencyConflict")
                    logger.LogWarning("Production task {TaskId} failed: {Error}", id, error);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Production task {TaskId} failed.", id);
            }
        }
    }
}
