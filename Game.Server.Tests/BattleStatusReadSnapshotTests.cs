using System.Data.Common;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class BattleStatusReadSnapshotTests
{
    [Fact]
    public async Task ReadSnapshotIncludesUnsavedChangesWithoutTrackingDatabaseRowsAndExpiresOnDispose()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var selects = new SnapshotSelectCounter();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite(connection).AddInterceptors(selects).Options);
        await db.Database.EnsureCreatedAsync();
        var room = new Room { Id = 7, RunSequence = 1 };
        db.BattleStatusEffects.AddRange(State(11), State(12), State(13));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var modified = await db.BattleStatusEffects.SingleAsync(effect => effect.TargetId == 11);
        modified.Stacks = 2;
        var deleted = await db.BattleStatusEffects.SingleAsync(effect => effect.TargetId == 12);
        db.Remove(deleted);
        db.Add(State(14));
        var statuses = new BattleStatusService(db, new BattleStatusCatalog(Options.Create(new MonsterCombatOptions
        {
            StatusEffects = [new() { Code = "resource", Name = "Resource", Description = "Resource", EffectType = "None",
                MaxStacks = 3, IsPositive = true, Lifetime = BattleStatusLifetime.Run }]
        })));
        selects.Count = 0;

        using (await statuses.BeginReadSnapshotAsync(room))
        {
            var descriptions = await statuses.DescribeManyAsync(room, "Character", [11, 12, 13, 14]);
            Assert.Equal(2, Assert.Single(descriptions[11]).Stacks);
            Assert.Empty(descriptions[12]);
            Assert.Single(descriptions[13]);
            Assert.Single(descriptions[14]);
            Assert.True(await statuses.HasRemovableAsync(room, "Character", [11, 13, 14], positive: true));
            Assert.Equal(1, selects.Count);
            Assert.DoesNotContain(db.ChangeTracker.Entries<BattleStatusEffect>(), entry => entry.Entity.TargetId == 13);
            Assert.Equal(EntityState.Deleted, db.Entry(deleted).State);
            Assert.Equal(EntityState.Modified, db.Entry(modified).State);
            Assert.Equal(3, db.ChangeTracker.Entries<BattleStatusEffect>().Count());
        }

        Assert.True(await statuses.HasAsync(room, "Character", 13, "resource"));
        Assert.Equal(2, selects.Count);
    }

    [Fact]
    public async Task ReadSnapshotCannotReplaceAnActiveSettlementOrLeakIntoAnotherRun()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var selects = new SnapshotSelectCounter();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite(connection).AddInterceptors(selects).Options);
        await db.Database.EnsureCreatedAsync();
        var room = new Room { Id = 7, RunSequence = 1 };
        db.Add(State(11));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var statuses = new BattleStatusService(db, new BattleStatusCatalog(Options.Create(new MonsterCombatOptions
        {
            StatusEffects = [new() { Code = "resource", Name = "Resource", Description = "Resource", EffectType = "None",
                MaxStacks = 3, IsPositive = true, Lifetime = BattleStatusLifetime.Run }]
        })));
        selects.Count = 0;
        using (await statuses.BeginSettlementAsync(room))
        using (await statuses.BeginReadSnapshotAsync(room))
        {
            await statuses.SetCounterAsync(room, "Character", 11, "resource", 3);
            Assert.Equal(3, await statuses.StacksAsync(room, "Character", 11, "resource"));
            Assert.Equal(1, selects.Count);
            Assert.False(await statuses.HasAsync(new Room { Id = room.Id, RunSequence = 2 }, "Character", 11, "resource"));
            Assert.Equal(2, selects.Count);
        }
        using (await statuses.BeginSettlementAsync(room))
            Assert.Equal(3, await statuses.StacksAsync(room, "Character", 11, "resource"));
    }

    private static BattleStatusEffect State(int targetId) => new()
    {
        RoomId = 7, RunSequence = 1, TargetType = "Character", TargetId = targetId,
        EffectCode = "resource", Stacks = 1, ExpiresAfterRound = 3, Lifetime = BattleStatusLifetime.Run
    };

    private sealed class SnapshotSelectCounter : DbCommandInterceptor
    {
        public int Count { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("\"BattleStatusEffects\"", StringComparison.Ordinal)) Count++;
            return ValueTask.FromResult(result);
        }
    }
}
