using Game.Server.Data;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public sealed class EconomicRequestMigrationTests
{
    private const string PreviousMigration = "20260930030000_AddAccountSessionIndexes";
    private const string ReceiptMigration = "20260930040000_AddEconomicRequestResults";

    [Fact]
    public async Task DownAndUpWithoutResultReceiptsPreservesExistingRequestRecords()
    {
        await using var store = new MigrationStore();
        await using (var db = store.Open())
        {
            await db.Database.MigrateAsync();
            db.LogisticsRequests.Add(new LogisticsRequest
            {
                CharacterId = 1, RequestId = "purchase", Kind = "ShopPurchase", Fingerprint = "potion:1",
                CompletedAtUtc = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc)
            });
            await db.SaveChangesAsync();
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            Assert.False(await HasResultColumn(db));
            Assert.DoesNotContain(ReceiptMigration, await db.Database.GetAppliedMigrationsAsync());
            await db.Database.MigrateAsync();
            Assert.True(await HasResultColumn(db));
            Assert.False(db.Database.HasPendingModelChanges());
            var receipt = await db.LogisticsRequests.SingleAsync();
            Assert.Equal("ShopPurchase", receipt.Kind);
            Assert.Equal("potion:1", receipt.Fingerprint);
            Assert.Null(receipt.ResultJson);
        }
    }

    [Fact]
    public async Task DownWithCommittedResultFailsBeforeDroppingSchemaOrReceiptAndDatabaseRemainsUsable()
    {
        await using var store = new MigrationStore();
        const string result = "{\"Name\":\"Original reward\",\"Quantity\":1,\"WeaponName\":\"Original weapon\"}";
        await using (var db = store.Open())
        {
            await db.Database.MigrateAsync();
            db.LogisticsRequests.Add(new LogisticsRequest
            {
                CharacterId = 1, RequestId = "committed-exchange", Kind = "DungeonExchange",
                Fingerprint = "offer", CompletedAtUtc = DateTime.UtcNow, ResultJson = result
            });
            await db.SaveChangesAsync();
            var failure = await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync(PreviousMigration));
            Assert.Contains("rollback blocked", failure.Message);
            Assert.Contains("pre-release database backup", failure.Message);
            Assert.True(await HasResultColumn(db));
            Assert.Contains(ReceiptMigration, await db.Database.GetAppliedMigrationsAsync());
            db.ChangeTracker.Clear();
            Assert.Equal(result, (await db.LogisticsRequests.SingleAsync()).ResultJson);
            // Running the current schema remains possible after the failed rollback,
            // including another migration check and a new committed result.
            await db.Database.MigrateAsync();
            db.LogisticsRequests.Add(new LogisticsRequest
            {
                CharacterId = 1, RequestId = "next-exchange", Kind = "DungeonExchange",
                Fingerprint = "next-offer", CompletedAtUtc = DateTime.UtcNow, ResultJson = result
            });
            await db.SaveChangesAsync();
        }
        await using var verify = store.Open();
        Assert.False(verify.Database.HasPendingModelChanges());
        Assert.True(await HasResultColumn(verify));
        Assert.Equal(2, await verify.LogisticsRequests.CountAsync());
        Assert.All(await verify.LogisticsRequests.ToListAsync(), receipt => Assert.Equal(result, receipt.ResultJson));
    }

    private static async Task<bool> HasResultColumn(GameDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('LogisticsRequests') WHERE name = 'ResultJson'";
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    private sealed class MigrationStore : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"idlegame-economic-migration-{Guid.NewGuid():N}.db");
        public GameDbContext Open() => new(new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite($"Data Source={_path};Pooling=False").Options);
        public ValueTask DisposeAsync()
        {
            File.Delete(_path);
            return ValueTask.CompletedTask;
        }
    }
}
