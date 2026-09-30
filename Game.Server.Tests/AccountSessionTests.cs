using System.Data.Common;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Auth;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class AccountSessionTests
{
    private const string PreviousMigration = "20260930020000_AddDungeonRunRuleSnapshots";

    [Fact]
    public async Task ConcurrentRegistrationCreatesOneAccountAndOneSession()
    {
        await using var store = new TestStore();
        await using (var setup = store.Open()) await setup.Database.EnsureCreatedAsync();
        var barrier = new RegistrationLookupBarrier();
        await using var first = store.Open(barrier);
        await using var second = store.Open(barrier);
        var results = await Task.WhenAll(
            Task.Run(() => Users(first).RegisterAsync(new RegisterRequest { UserName = " player ", Password = "secret" })),
            Task.Run(() => Users(second).RegisterAsync(new RegisterRequest { UserName = "player", Password = "secret" })));
        Assert.Single(results, result => result.Response is not null && result.Error is null);
        Assert.Single(results, result => result.Response is null && result.Error == "DuplicateUserName");
        var losingContext = results[0].Error is not null ? first : second;
        Assert.Empty(losingContext.ChangeTracker.Entries());
        await using var verify = store.Open();
        Assert.Equal("player", (await verify.Users.SingleAsync()).UserName);
        Assert.Single(await verify.UserLoginSessions.ToListAsync());
        // A failed registration must not poison the request scope's tracked state.
        Assert.Null((await Users(losingContext).RegisterAsync(new RegisterRequest
            { UserName = "another", Password = "secret" })).Error);
    }

    [Fact]
    public async Task RegistrationAndLoginKeepTrimmedCaseSensitiveNames()
    {
        await using var store = new TestStore();
        await using var db = store.Open();
        await db.Database.EnsureCreatedAsync();
        var users = Users(db);
        Assert.Null((await users.RegisterAsync(new RegisterRequest { UserName = " Player ", Password = "secret" })).Error);
        Assert.Null((await users.RegisterAsync(new RegisterRequest { UserName = "player", Password = "secret" })).Error);
        Assert.Equal("DuplicateUserName", (await users.RegisterAsync(new RegisterRequest
            { UserName = "Player", Password = "secret" })).Error);
        Assert.Equal("Player", (await users.LoginAsync(new LoginRequest
            { UserName = " Player ", Password = "secret" })).Response!.UserName);
        Assert.Equal("InvalidCredentials", (await users.LoginAsync(new LoginRequest
            { UserName = "PLAYER", Password = "secret" })).Error);
    }

    [Fact]
    public async Task ExistingDatabaseMigratesWithoutChangingAccountsOrSessionsAndUsesIndexes()
    {
        await using var store = new TestStore();
        var now = DateTime.UtcNow;
        await using (var legacy = store.Open())
        {
            await legacy.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            legacy.Users.AddRange(new User { UserName = "Player", PasswordHash = "hash" },
                new User { UserName = "player", PasswordHash = "other-hash" });
            legacy.UserLoginSessions.Add(new UserLoginSession
                { UserId = 1, Token = "old-token", CreatedAt = now, ExpireAt = now.AddDays(1) });
            await legacy.SaveChangesAsync();
        }
        await using var db = store.Open();
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(2, await db.Users.CountAsync());
        var session = await db.UserLoginSessions.SingleAsync();
        Assert.Equal("old-token", session.Token);
        Assert.Equal(now.AddDays(1), session.ExpireAt);
        Assert.Contains("IX_Users_UserName", await QueryPlan(db, "SELECT Id FROM Users WHERE UserName = 'Player'"));
        Assert.Contains("IX_UserLoginSessions_Token", await QueryPlan(db, "SELECT Id FROM UserLoginSessions WHERE Token = 'old-token'"));
        Assert.Contains("IX_UserLoginSessions_ExpireAt", await QueryPlan(db,
            "SELECT Id FROM UserLoginSessions WHERE ExpireAt <= '2026-01-01' ORDER BY ExpireAt LIMIT 500"));
        db.UserLoginSessions.Add(new UserLoginSession
            { UserId = 2, Token = "old-token", CreatedAt = now, ExpireAt = now.AddDays(2) });
        Assert.Equal(2067, Assert.IsType<SqliteException>(
            (await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqliteExtendedErrorCode);
    }

    [Theory]
    [InlineData(true, "duplicate Users.UserName")]
    [InlineData(false, "duplicate UserLoginSessions.Token")]
    public async Task MigrationRejectsLegacyDuplicatesWithExplanationAndPreservesData(bool duplicateNames, string explanation)
    {
        await using var store = new TestStore();
        await using (var legacy = store.Open())
        {
            await legacy.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            legacy.Users.AddRange(new User { UserName = "duplicate", PasswordHash = "one" },
                new User { UserName = duplicateNames ? "duplicate" : "other", PasswordHash = "two" });
            legacy.UserLoginSessions.AddRange(new UserLoginSession { UserId = 1, Token = "same" },
                new UserLoginSession { UserId = 2, Token = duplicateNames ? "different" : "same" });
            await legacy.SaveChangesAsync();
        }
        await using (var db = store.Open())
        {
            var failure = await Assert.ThrowsAsync<SqliteException>(() => db.Database.MigrateAsync());
            Assert.Contains(explanation, failure.Message);
        }
        await using var verify = store.Open();
        Assert.Equal(2, await verify.Users.CountAsync());
        Assert.Equal(2, await verify.UserLoginSessions.CountAsync());
        Assert.DoesNotContain("20260930030000_AddAccountSessionIndexes", await verify.Database.GetAppliedMigrationsAsync());
        // An operator can resolve the duplicate explicitly and retry the same migration.
        await verify.Users.Where(user => user.Id == 2)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.UserName, "resolved"));
        await verify.UserLoginSessions.Where(session => session.UserId == 2)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.Token, "resolved-token"));
        await verify.Database.MigrateAsync();
        Assert.Equal(2, await verify.Users.CountAsync());
        Assert.Equal(2, await verify.UserLoginSessions.CountAsync());
    }

    [Fact]
    public async Task CleanupIsBoundedAndOnlyDeletesExpiredSessionsIncludingExactCutoff()
    {
        await using var store = new TestStore();
        var now = DateTime.UtcNow;
        await using (var setup = store.Open())
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Users.Add(new User { Id = 1, UserName = "session-owner", PasswordHash = "hash" });
            setup.UserLoginSessions.AddRange(Enumerable.Range(0, 5).Select(index => new UserLoginSession
                { UserId = 1, Token = $"expired-{index}", ExpireAt = now.AddMinutes(index - 4) }));
            setup.UserLoginSessions.Add(new UserLoginSession { UserId = 1, Token = "valid", ExpireAt = now.AddDays(1) });
            await setup.SaveChangesAsync();
        }
        using var services = new ServiceCollection().AddDbContext<GameDbContext>(options => options.UseSqlite(store.ConnectionString))
            .BuildServiceProvider();
        using var cleanup = new SessionCleanupService(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SessionCleanupOptions { BatchSize = 2 }), NullLogger<SessionCleanupService>.Instance);
        Assert.Equal(2, await cleanup.CleanupBatchAsync(now));
        await using (var verify = store.Open()) Assert.Equal(4, await verify.UserLoginSessions.CountAsync());
        Assert.Equal(2, await cleanup.CleanupBatchAsync(now));
        Assert.Equal(1, await cleanup.CleanupBatchAsync(now));
        Assert.Equal(0, await cleanup.CleanupBatchAsync(now));
        await using var last = store.Open();
        Assert.Equal("valid", (await last.UserLoginSessions.SingleAsync()).Token);
        Assert.Null((await Users(last).GetCurrentUserEntityAsync("valid")).Error);
        Assert.Equal("Unauthorized", (await Users(last).GetCurrentUserEntityAsync("expired-4")).Error);
    }

    [Fact]
    public async Task CleanupHonorsCancellationWithoutDeletingSessions()
    {
        await using var store = new TestStore();
        await using var db = store.Open();
        await db.Database.EnsureCreatedAsync();
        db.UserLoginSessions.Add(new UserLoginSession { Token = "expired", ExpireAt = DateTime.UtcNow.AddDays(-1) });
        await db.SaveChangesAsync();
        using var services = new ServiceCollection().AddDbContext<GameDbContext>(options => options.UseSqlite(store.ConnectionString))
            .BuildServiceProvider();
        using var cleanup = new SessionCleanupService(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SessionCleanupOptions()), NullLogger<SessionCleanupService>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cleanup.CleanupBatchAsync(DateTime.UtcNow, new CancellationToken(true)));
        Assert.Equal(1, await db.UserLoginSessions.CountAsync());
    }

    private static UserService Users(GameDbContext db) => new(db, ProgressionTestFactory.Create(), SkillTestFactory.Create());

    private static async Task<string> QueryPlan(GameDbContext db, string sql)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        await using var reader = await command.ExecuteReaderAsync();
        var details = new List<string>();
        while (await reader.ReadAsync()) details.Add(reader.GetString(3));
        return string.Join("\n", details);
    }

    private sealed class RegistrationLookupBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothLookedUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _lookups;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("WHERE", StringComparison.Ordinal) &&
                command.CommandText.Contains("\"UserName\" =", StringComparison.Ordinal))
            {
                var lookups = Interlocked.Increment(ref _lookups);
                if (lookups == 2) _bothLookedUp.TrySetResult();
                if (lookups <= 2) await _bothLookedUp.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    private sealed class TestStore : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"idlegame-accounts-{Guid.NewGuid():N}.db");
        public string ConnectionString => $"Data Source={_path};Pooling=False";
        public GameDbContext Open(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite(ConnectionString).AddInterceptors(interceptors).Options);
        public ValueTask DisposeAsync()
        {
            File.Delete(_path);
            return ValueTask.CompletedTask;
        }
    }
}
