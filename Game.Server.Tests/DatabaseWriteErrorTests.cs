using System.Text.Json;
using Game.Server.Data;
using Game.Server.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Game.Server.Tests;

public sealed class DatabaseWriteErrorTests
{
    [Fact]
    public async Task RealUniqueConstraintIsAConflictButInvalidStoredValuesAreNot()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE Items (Id INTEGER PRIMARY KEY, Quantity INTEGER CHECK(Quantity >= 0)); INSERT INTO Items VALUES(1, 1);";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "INSERT INTO Items VALUES(1, 1);";
        var duplicate = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        Assert.True(DatabaseWriteErrors.IsConflict(new DbUpdateException("save failed", duplicate)));
        command.CommandText = "UPDATE Items SET Quantity = -1 WHERE Id = 1;";
        var invalid = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        Assert.False(DatabaseWriteErrors.IsConflict(new DbUpdateException("save failed", invalid)));
        command.CommandText = "SELECT Quantity FROM Items WHERE Id = 1;";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(5, 503, "DatabaseBusy")]
    [InlineData(10, 500, "DatabaseFailure")]
    public async Task HttpDatabaseFailureIsDiagnosableWithoutLeakingPrivateDetails(int code, int status, string title)
    {
        var context = new DefaultHttpContext { TraceIdentifier = "request-123" };
        context.Response.Body = new MemoryStream();
        var exception = new DbUpdateException("secret connection string", new SqliteException("private SQL and token", code));
        var handler = new DatabaseExceptionHandler(NullLogger<DatabaseExceptionHandler>.Instance);
        Assert.True(await handler.TryHandleAsync(context, exception, CancellationToken.None));
        Assert.Equal(status, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(title, json.RootElement.GetProperty("title").GetString());
        Assert.Equal("request-123", json.RootElement.GetProperty("traceId").GetString());
        Assert.DoesNotContain("secret", json.RootElement.GetRawText());
        Assert.DoesNotContain("private", json.RootElement.GetRawText());
        Assert.Equal(code == 5 ? "1" : "", context.Response.Headers.RetryAfter.ToString());
    }
}

public partial class BattleServiceTests
{
    [Fact]
    public async Task StorageFailureDoesNotBecomeConcurrencyConflictOrPublishUncommittedBattle()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var failure = new DbUpdateException("disk failed", new SqliteException("disk I/O failure", 10));
        var options = new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite(test.Db.Database.GetConnectionString()!)
            .AddInterceptors(new FailingBattleSave(failure)).Options;
        await using var db = new GameDbContext(options);
        var history = new Game.Server.Services.BattleLogStore();
        var service = EventService(db, history);
        Assert.Same(failure, await Assert.ThrowsAsync<DbUpdateException>(() => service.StartPreparationAsync(1, test.Token)));
        Assert.Empty(history.GetEvents(1));
        Assert.Empty(history.Get(1));
        await using var verify = test.CreateDbContext();
        Assert.Equal(0, (await verify.Rooms.SingleAsync()).RoundNumber);
        Assert.Equal(test.Monster.Hp, (await verify.Monsters.SingleAsync()).Hp);
    }

    private sealed class FailingBattleSave(Exception failure) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) => throw failure;
    }
}
