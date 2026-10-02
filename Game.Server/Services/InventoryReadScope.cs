using System.Data;
using System.Data.Common;
using Game.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Game.Server.Services;

/// <summary>A deferred SQLite snapshot: inventory reads do not take a reserved writer lock.</summary>
internal sealed class InventoryReadScope(GameDbContext db, DbTransaction transaction, IDbContextTransaction adapter) : IAsyncDisposable
{
    public static async Task<InventoryReadScope?> BeginAsync(GameDbContext db, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is not null) return null;
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = db.Database.GetDbConnection();
            var transaction = connection is SqliteConnection sqlite
                ? sqlite.BeginTransaction(IsolationLevel.Serializable, deferred: true)
                : await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            try
            {
                var adapter = await db.Database.UseTransactionAsync(transaction, ct);
                return new(db, transaction, adapter!);
            }
            catch { await transaction.DisposeAsync(); throw; }
        }
        catch { await db.Database.CloseConnectionAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        await adapter.DisposeAsync();
        await transaction.DisposeAsync();
        await db.Database.CloseConnectionAsync();
    }
}
