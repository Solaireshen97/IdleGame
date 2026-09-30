using Game.Server.Data;
using Game.Shared;
using Game.Shared.Enums;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class RoomCycleService(IServiceScopeFactory scopeFactory, ILogger<RoomCycleService> logger,
    BackgroundCycleHealth? health = null, TimeProvider? timeProvider = null) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        health?.Register(BackgroundCycleHealth.Rooms);
        using var timer = new PeriodicTimer(ScanInterval, clock);
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
                    stoppingToken.ThrowIfCancellationRequested();
                    health?.FailScan(BackgroundCycleHealth.Rooms);
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
        var now = clock.GetUtcNow().UtcDateTime;
        var repeatCutoff = now.AddSeconds(-BattleRules.RepeatBattleDelaySeconds);
        var roomIds = await dbContext.Rooms.AsNoTracking()
            .Where(room => dbContext.RoomOperations.Any(operation => operation.RoomId == room.Id && operation.Status == "Pending") ||
                room.ClosedAtUtc == null && (room.Status == RoomStatus.NotStarted ||
                room.Status == RoomStatus.Preparing ||
                room.Status == RoomStatus.Cooldown && room.NextRoundAvailableAtUtc <= now ||
                room.Status == RoomStatus.WaveTransition && room.NextRoundAvailableAtUtc <= now ||
                room.IsRepeatBattle && room.ExpiresAtUtc <= now && room.Status == RoomStatus.BattleOver ||
                room.Status == RoomStatus.BattleOver && room.IsRepeatBattle && room.BattleEndedAtUtc <= repeatCutoff))
            .Select(room => room.Id)
            .ToListAsync(cancellationToken);

        var failedItems = 0;
        foreach (var roomId in roomIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var roomScope = scopeFactory.CreateScope();
                var battleService = roomScope.ServiceProvider.GetRequiredService<BattleService>();
                var (_, error) = await battleService.SyncRoomAsync(roomId);
                if (error is not null && error is not ("ConcurrencyConflict" or "NotFound"))
                {
                    failedItems++;
                    logger.LogWarning("Automatic battle progress for room {RoomId} failed: {Error}", roomId, error);
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
                logger.LogError(exception, "Automatic battle progress for room {RoomId} failed.", roomId);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        health?.CompleteScan(BackgroundCycleHealth.Rooms, roomIds.Count, failedItems);
    }
}
