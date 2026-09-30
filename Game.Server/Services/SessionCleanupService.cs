using Game.Server.Configuration;
using Game.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class SessionCleanupService(IServiceScopeFactory scopes,
    IOptions<SessionCleanupOptions> options, ILogger<SessionCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(
            Math.Clamp(options.Value.IntervalMinutes, 1, 1440)));
        try
        {
            do
            {
                try
                {
                    var deleted = await CleanupBatchAsync(DateTime.UtcNow, stoppingToken);
                    if (deleted > 0) logger.LogDebug("Removed {Count} expired login sessions.", deleted);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(exception, "Failed to clean up expired login sessions.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task<int> CleanupBatchAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        var ids = await db.UserLoginSessions.AsNoTracking()
            .Where(session => session.ExpireAt <= cutoffUtc)
            .OrderBy(session => session.ExpireAt).ThenBy(session => session.Id)
            .Take(Math.Clamp(options.Value.BatchSize, 1, 2000))
            .Select(session => session.Id).ToArrayAsync(cancellationToken);
        if (ids.Length == 0) return 0;
        return await db.UserLoginSessions
            .Where(session => ids.Contains(session.Id) && session.ExpireAt <= cutoffUtc)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
