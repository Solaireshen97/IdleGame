using Game.Server.Data;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Infrastructure;

public sealed class DatabaseExceptionHandler(ILogger<DatabaseExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not (DbUpdateException or SqliteException)) return false;
        var busy = DatabaseWriteErrors.IsBusy(exception);
        var status = busy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError;
        if (busy)
        {
            logger.LogWarning(exception, "Database busy for request {TraceId}.", context.TraceIdentifier);
            context.Response.Headers.RetryAfter = "1";
        }
        else logger.LogError(exception, "Database failure for request {TraceId}.", context.TraceIdentifier);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = busy ? "DatabaseBusy" : "DatabaseFailure",
            Extensions = { ["traceId"] = context.TraceIdentifier }
        }, cancellationToken);
        return true;
    }
}
