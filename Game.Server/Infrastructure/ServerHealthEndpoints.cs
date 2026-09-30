using Game.Server.Data;
using Game.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Infrastructure;

public static class ServerHealthEndpoints
{
    public static void MapServerHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/liveness", ["GET", "HEAD"], (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { status = "Healthy" });
        })
            .AllowAnonymous();
        endpoints.MapMethods("/readiness", ["GET", "HEAD"], CheckReadinessAsync).AllowAnonymous();
        endpoints.MapMethods("/health", ["GET", "HEAD"], CheckReadinessAsync).AllowAnonymous();
    }

    private static async Task<IResult> CheckReadinessAsync(GameDbContext db, BackgroundCycleHealth health,
        ILoggerFactory loggerFactory, HttpContext context)
    {
        var databaseHealthy = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            db.Database.SetCommandTimeout(3);
            // Exercise the application schema as well as the connection, without loading account data.
            await db.Users.AsNoTracking().Select(user => user.Id).Take(1).ToListAsync(timeout.Token);
            databaseHealthy = true;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger("ServerHealth").LogError(exception,
                "Readiness database probe failed. TraceId: {TraceId}", context.TraceIdentifier);
        }

        var workers = health.Snapshot();
        var ready = databaseHealthy && workers.Any(worker => worker.Name == BackgroundCycleHealth.Rooms)
            && workers.Any(worker => worker.Name == BackgroundCycleHealth.Production)
            && workers.All(worker => worker.Status == "Healthy");
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(new
        {
            status = ready ? "Healthy" : "Unhealthy",
            database = databaseHealthy ? "Healthy" : "Unhealthy",
            backgroundTasks = workers
        }, statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }
}
