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

public sealed class BattleStatusSettlementScopeTests
{
    [Fact]
    public async Task SettlementLoadsOnceAndReadsTrackedChangesWithoutFurtherStatusSelects()
    {
        await using var test = await Context.CreateAsync();
        await test.Statuses.SetCounterAsync(test.Room, "Character", 11, "charges", 2);
        await test.Statuses.ApplyAsync(test.Room, "Monster", 51, "poison", 3, [], "Enemy", 7);
        await test.Db.SaveChangesAsync();
        test.Db.ChangeTracker.Clear();
        test.Room.RoundNumber = 1;
        test.Selects.Count = 0;

        using (await test.Statuses.BeginSettlementAsync(test.Room))
        {
            Assert.Equal(1, test.Selects.Count);
            for (var hit = 0; hit < 10; hit++)
            {
                Assert.True(await test.Statuses.HasAsync(test.Room, "Character", 11, "charges"));
                Assert.Equal(2, await test.Statuses.StacksAsync(test.Room, "Character", 11, "charges"));
                Assert.Single(await test.Statuses.GetActiveAsync(test.Room, "Monster", [51]));
                await test.Statuses.ModifierAsync(test.Room, "Character", 11, "None");
                await test.Statuses.MechanicPowerAsync(test.Room, "Character", 11, BattleStatusMechanic.NormalAttackEcho);
            }
            await test.Statuses.ApplyAsync(test.Room, "Monster", 51, "poison", 3, [], "Enemy", 9);
            var monster = new Monster { Id = 51, Hp = 100, MaxHp = 100 };
            await test.Statuses.ResolveEndOfRoundAsync(test.Room, monster, [], []);
            Assert.Equal(91, monster.Hp);
            await test.Statuses.SetCounterAsync(test.Room, "Character", 12, "resource", 2,
                boundTargetType: "Monster", boundTargetId: 51);
            Assert.Equal(1, test.Selects.Count);
            await test.Statuses.RemoveBoundToAsync(test.Room.Id, "Monster", 51);
            var selectsAfterTargetCleanup = test.Selects.Count;
            Assert.False(await test.Statuses.HasAsync(test.Room, "Character", 12, "resource"));
            Assert.False(await test.Statuses.HasAsync(test.Room, "Monster", 51, "poison"));
            Assert.True(await test.Statuses.HasAsync(test.Room, "Character", 11, "charges"));
            await test.Statuses.ClearRunAsync(test.Room);
            Assert.Empty(await test.Statuses.GetActiveAsync(test.Room, "Character", [11, 12]));
            Assert.Equal(selectsAfterTargetCleanup, test.Selects.Count);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddedOrPersistedCounterCanBeConsumedAndRegrantedBeforeSave(bool persisted)
    {
        await using var test = await Context.CreateAsync();
        if (persisted)
        {
            await test.Statuses.SetCounterAsync(test.Room, "Character", 11, "charges", 2);
            await test.Db.SaveChangesAsync();
            test.Db.ChangeTracker.Clear();
        }
        test.Selects.Count = 0;
        using (await test.Statuses.BeginSettlementAsync(test.Room))
        {
            if (!persisted) await test.Statuses.SetCounterAsync(test.Room, "Character", 11, "charges", 2);
            Assert.Equal(1, await test.Statuses.ConsumeAsync(test.Room, "Character", 11, "charges", 1));
            Assert.Equal(1, await test.Statuses.StacksAsync(test.Room, "Character", 11, "charges"));
            Assert.Equal(1, await test.Statuses.ConsumeAsync(test.Room, "Character", 11, "charges"));
            Assert.False(await test.Statuses.HasAsync(test.Room, "Character", 11, "charges"));
            await test.Statuses.SetCounterAsync(test.Room, "Character", 11, "charges", 3);
            Assert.Equal(3, await test.Statuses.StacksAsync(test.Room, "Character", 11, "charges"));
            Assert.Equal(1, test.Selects.Count);
            await test.Db.SaveChangesAsync();
        }
        test.Db.ChangeTracker.Clear();
        using (await test.Statuses.BeginSettlementAsync(test.Room))
            Assert.Equal(3, Assert.Single(await test.Statuses.GetActiveAsync(test.Room, "Character", [11])).Stacks);
        Assert.Single(await test.Db.BattleStatusEffects.ToListAsync());
    }

    [Fact]
    public async Task ExpiredCodeIsLoadedAndRefreshedInPlace()
    {
        await using var test = await Context.CreateAsync();
        await test.Statuses.ApplyAsync(test.Room, "Monster", 51, "poison", 1, [], "Enemy", 7);
        await test.Db.SaveChangesAsync();
        var originalId = Assert.Single(test.Db.BattleStatusEffects.Local).Id;
        test.Db.ChangeTracker.Clear();
        test.Room.RoundNumber = 2;
        test.Selects.Count = 0;
        using (await test.Statuses.BeginSettlementAsync(test.Room))
        {
            Assert.False(await test.Statuses.HasAsync(test.Room, "Monster", 51, "poison"));
            await test.Statuses.ApplyAsync(test.Room, "Monster", 51, "poison", 3, [], "Enemy", 9);
            var refreshed = Assert.Single(await test.Statuses.GetActiveAsync(test.Room, "Monster", [51]));
            Assert.Equal(originalId, refreshed.Id);
            Assert.Equal(2, refreshed.AppliedRound);
            Assert.Equal(5, refreshed.ExpiresAfterRound);
            Assert.Equal(9, refreshed.PerTickValue);
            Assert.Equal(1, test.Selects.Count);
            await test.Db.SaveChangesAsync();
        }
        Assert.Single(await test.Db.BattleStatusEffects.ToListAsync());
    }

    [Fact]
    public async Task ScopeExitAndDifferentRoomOrRunUseTheirOwnDatabaseReads()
    {
        await using var test = await Context.CreateAsync();
        var nextRun = new Room { Id = test.Room.Id, RunSequence = 2 };
        var otherRoom = new Room { Id = 8, RunSequence = 1 };
        await test.Statuses.SetCounterAsync(test.Room, "Character", 11, "charges", 1);
        await test.Statuses.SetCounterAsync(nextRun, "Character", 11, "charges", 2);
        await test.Statuses.SetCounterAsync(otherRoom, "Character", 11, "charges", 3);
        await test.Db.SaveChangesAsync();
        test.Db.ChangeTracker.Clear();
        test.Selects.Count = 0;
        using (await test.Statuses.BeginSettlementAsync(test.Room))
        {
            Assert.Equal(1, await test.Statuses.StacksAsync(test.Room, "Character", 11, "charges"));
            Assert.Equal(2, await test.Statuses.StacksAsync(nextRun, "Character", 11, "charges"));
            Assert.Equal(3, await test.Statuses.StacksAsync(otherRoom, "Character", 11, "charges"));
            Assert.Equal(3, test.Selects.Count);
        }
        await test.Statuses.StacksAsync(test.Room, "Character", 11, "charges");
        Assert.Equal(4, test.Selects.Count);
        test.Db.ChangeTracker.Clear();
        using (await test.Statuses.BeginSettlementAsync(nextRun))
            Assert.Equal(2, await test.Statuses.StacksAsync(nextRun, "Character", 11, "charges"));
        Assert.Equal(5, test.Selects.Count);
    }

    [Fact]
    public async Task OverlapIsRejectedAndFailedSettlementCanReloadAfterTrackerIsCleared()
    {
        await using var test = await Context.CreateAsync();
        await test.Statuses.SetCounterAsync(test.Room, "Character", 11, "charges", 2);
        await test.Db.SaveChangesAsync();
        test.Db.ChangeTracker.Clear();
        test.Selects.Count = 0;
        using (await test.Statuses.BeginSettlementAsync(test.Room))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.Statuses.BeginSettlementAsync(test.Room));
            await test.Statuses.ConsumeAsync(test.Room, "Character", 11, "charges");
            test.Db.ChangeTracker.Clear();
        }
        using (await test.Statuses.BeginSettlementAsync(test.Room))
            Assert.Equal(2, await test.Statuses.StacksAsync(test.Room, "Character", 11, "charges"));
        Assert.Equal(2, test.Selects.Count);
    }

    [Fact]
    public async Task FailedInitialLoadReleasesScopeSoTheNextSettlementCanRetry()
    {
        await using var test = await Context.CreateAsync();
        await test.Statuses.SetCounterAsync(test.Room, "Character", 11, "charges", 2);
        await test.Db.SaveChangesAsync();
        test.Db.ChangeTracker.Clear();
        test.Selects.Count = 0;
        test.Selects.FailNextSelect = true;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => test.Statuses.BeginSettlementAsync(test.Room));
        Assert.Equal("Simulated status load failure", failure.Message);
        test.Db.ChangeTracker.Clear();
        using (await test.Statuses.BeginSettlementAsync(test.Room))
            Assert.Equal(2, await test.Statuses.StacksAsync(test.Room, "Character", 11, "charges"));
        Assert.Equal(2, test.Selects.Count);
    }

    private sealed class StatusSelectCounter : DbCommandInterceptor
    {
        public int Count { get; set; }
        public bool FailNextSelect { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("BattleStatusEffects", StringComparison.Ordinal))
            {
                Count++;
                if (FailNextSelect)
                {
                    FailNextSelect = false;
                    throw new InvalidOperationException("Simulated status load failure");
                }
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Context(SqliteConnection connection, GameDbContext db, StatusSelectCounter selects) : IAsyncDisposable
    {
        public GameDbContext Db { get; } = db;
        public StatusSelectCounter Selects { get; } = selects;
        public Room Room { get; } = new() { Id = 7, RunSequence = 1 };
        public BattleStatusService Statuses { get; } = new(db, new BattleStatusCatalog(Options.Create(new MonsterCombatOptions
        {
            StatusEffects =
            [
                new() { Code = "charges", Name = "Charges", Description = "Skill charges", EffectType = "None", MaxStacks = 3,
                    Lifetime = BattleStatusLifetime.UntilConsumed, CounterKind = BattleStatusCounterKind.Charges, IsPositive = true },
                new() { Code = "resource", Name = "Resource", Description = "Bound resource", EffectType = "None", MaxStacks = 10,
                    Lifetime = BattleStatusLifetime.Encounter, IsPositive = true },
                new() { Code = "poison", Name = "Poison", Description = "Periodic damage", EffectType = "DamageOverTime" }
            ]
        })));

        public static async Task<Context> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var selects = new StatusSelectCounter();
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>()
                .UseSqlite(connection).AddInterceptors(selects).Options);
            await db.Database.EnsureCreatedAsync();
            return new(connection, db, selects);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
