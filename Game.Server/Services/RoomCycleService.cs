using Game.Server.Data;
using Game.Shared;
using Game.Shared.Enums;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class RoomCycleService(IServiceScopeFactory scopeFactory, ILogger<RoomCycleService> logger) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ScanInterval);
        try
        {
            do
            {
                try
                {
                    await AdvanceRoomsAsync(stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(exception, "Failed to scan rooms for automatic battle progress.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task AdvanceRoomsAsync(CancellationToken cancellationToken)
    {
        using var listScope = scopeFactory.CreateScope();
        var dbContext = listScope.ServiceProvider.GetRequiredService<GameDbContext>();
        var now = DateTime.UtcNow;
        var repeatCutoff = now.AddSeconds(-BattleRules.RepeatBattleDelaySeconds);
        var roomIds = await dbContext.Rooms.AsNoTracking()
            .Where(room => room.Status == RoomStatus.NotStarted ||
                room.Status == RoomStatus.Preparing ||
                room.Status == RoomStatus.Cooldown && room.NextRoundAvailableAtUtc <= now ||
                room.Status == RoomStatus.BattleOver && room.IsRepeatBattle && room.BattleEndedAtUtc <= repeatCutoff)
            .Select(room => room.Id)
            .ToListAsync(cancellationToken);

        foreach (var roomId in roomIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var roomScope = scopeFactory.CreateScope();
                var battleService = roomScope.ServiceProvider.GetRequiredService<BattleService>();
                var (_, error) = await battleService.SyncRoomAsync(roomId);
                if (error is not null && error is not ("ConcurrencyConflict" or "NotFound"))
                    logger.LogWarning("Automatic battle progress for room {RoomId} failed: {Error}", roomId, error);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Automatic battle progress for room {RoomId} failed.", roomId);
            }
        }
    }
}
