using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Data;

/// <summary>Only expected competing writes should be reported as stale game state.</summary>
public static class DatabaseWriteErrors
{
    public static bool IsConflict(Exception exception) =>
        exception is DbUpdateConcurrencyException || IsUniqueConstraint(exception);

    public static bool IsUniqueConstraint(Exception exception) =>
        SqliteCause(exception)?.SqliteExtendedErrorCode is 1555 or 2067; // PRIMARYKEY / UNIQUE

    public static bool IsBusy(Exception exception) =>
        SqliteCause(exception)?.SqliteErrorCode is 5 or 6; // BUSY / LOCKED, including extended variants

    private static SqliteException? SqliteCause(Exception exception) => exception switch
    {
        SqliteException sqlite => sqlite,
        DbUpdateException { InnerException: { } inner } => SqliteCause(inner),
        _ => null
    };
}
