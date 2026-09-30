using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class BattleStatusSystemTests
{
    [Fact]
    public async Task ZeroSubsequentRoundsExpressesCurrentRoundAndExpiresAtItsEnd()
    {
        await using var test = await Context.CreateAsync();
        await test.Statuses.ApplyAsync(test.Room, "Character", 11, "poison", 0, [], "Player", 7);
        var displayed = Assert.Single(await test.Statuses.DescribeAsync(test.Room, "Character", 11));
        Assert.Equal("本回合有效", displayed.DurationText);
        await test.Statuses.ResolveEndOfRoundAsync(test.Room, new Monster { Id = 51 }, [], []);
        Assert.False(await test.Statuses.HasAsync(test.Room, "Character", 11, "poison"));
    }

    [Fact]
    public async Task CounterConsumptionAndRegrantAreVisibleBeforeSavingAndSurviveReload()
    {
        await using var test = await Context.CreateAsync();
        await test.Statuses.SetCounterAsync(test.Room, "Character", 11, "charges", 2,
            new("Character", 11, "empower"));
        var beforeSave = Assert.Single(await test.Statuses.DescribeAsync(test.Room, "Character", 11));
        Assert.Equal("剩余 2 次", beforeSave.CounterText);
        Assert.Equal("empower", beforeSave.SourceSkillCode);
        Assert.Contains("消耗后移除", beforeSave.DurationText);
        Assert.Equal(1, await test.Statuses.ConsumeAsync(test.Room, "Character", 11, "charges", 1));
        await test.Db.SaveChangesAsync();
        test.Db.ChangeTracker.Clear();
        Assert.Equal(1, await test.Statuses.StacksAsync(test.Room, "Character", 11, "charges"));
        Assert.Equal(1, await test.Statuses.ConsumeAsync(test.Room, "Character", 11, "charges"));
        Assert.False(await test.Statuses.HasAsync(test.Room, "Character", 11, "charges"));
        await test.Statuses.SetCounterAsync(test.Room, "Character", 11, "charges", 3);
        await test.Db.SaveChangesAsync();
        Assert.Equal(3, Assert.Single(await test.Db.BattleStatusEffects.ToListAsync()).Stacks);
    }

    [Fact]
    public async Task EncounterCleanupRemovesBoundResourcesAndEnemyEffectsButPreservesOtherOwnersAndRunBuffs()
    {
        await using var test = await Context.CreateAsync();
        await test.Statuses.SetCounterAsync(test.Room, "Character", 11, "resource", 2,
            new("Character", 11), "Monster", 51);
        await test.Statuses.SetCounterAsync(test.Room, "Character", 12, "resource", 1,
            new("Character", 12), "Monster", 52);
        await test.Statuses.SetCounterAsync(test.Room, "Character", 11, "charges", 1);
        await test.Statuses.ApplyAsync(test.Room, "Monster", 51, "poison", 3, [], "Enemy", 7,
            new("Character", 11, "poison-skill"));
        await test.Db.SaveChangesAsync();
        await test.Statuses.RemoveBoundToAsync(test.Room.Id, "Monster", 51);
        Assert.False(await test.Statuses.HasAsync(test.Room, "Character", 11, "resource"));
        Assert.False(await test.Statuses.HasAsync(test.Room, "Monster", 51, "poison"));
        Assert.True(await test.Statuses.HasAsync(test.Room, "Character", 12, "resource"));
        Assert.True(await test.Statuses.HasAsync(test.Room, "Character", 11, "charges"));
        await test.Db.SaveChangesAsync();
        Assert.Equal(2, await test.Db.BattleStatusEffects.CountAsync());
    }

    [Theory]
    [InlineData(BattleStatusSnapshotRefresh.KeepStronger, 9)]
    [InlineData(BattleStatusSnapshotRefresh.Replace, 4)]
    public async Task SnapshotRefreshPolicyIsDefinedByTheStatusAndKeepsTheDueTick(
        BattleStatusSnapshotRefresh policy, int expected)
    {
        await using var test = await Context.CreateAsync(policy);
        await test.Statuses.ApplyAsync(test.Room, "Monster", 51, "poison", 3, [], "Enemy", 9, new("Character", 11, "first"));
        await test.Db.SaveChangesAsync();
        test.Room.RoundNumber = 1;
        await test.Statuses.ApplyAsync(test.Room, "Monster", 51, "poison", 1, [], "Enemy", 4, new("Character", 12, "second"));
        var instance = Assert.Single(await test.Statuses.GetActiveAsync(test.Room, "Monster", [51]));
        Assert.Equal(expected, instance.PerTickValue);
        Assert.Equal(0, instance.AppliedRound);
        Assert.Equal(3, instance.ExpiresAfterRound);
        Assert.Null(instance.BoundTargetId);
        Assert.Equal(policy == BattleStatusSnapshotRefresh.Replace ? 12 : 11, instance.SourceActorId);
        Assert.Equal(policy == BattleStatusSnapshotRefresh.Replace ? "second" : "first", instance.SourceSkillCode);
    }

    private sealed class Context(SqliteConnection connection, GameDbContext db, BattleStatusService statuses) : IAsyncDisposable
    {
        public GameDbContext Db { get; } = db;
        public BattleStatusService Statuses { get; } = statuses;
        public Room Room { get; } = new() { Id = 7, RunSequence = 1 };

        public static async Task<Context> CreateAsync(BattleStatusSnapshotRefresh refresh = BattleStatusSnapshotRefresh.KeepStronger)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var catalog = new BattleStatusCatalog(Options.Create(new MonsterCombatOptions
            {
                StatusEffects =
                [
                    new() { Code = "charges", Name = "强化", Description = "下一次本职技能强化", EffectType = "None",
                        MaxStacks = 3, Lifetime = BattleStatusLifetime.UntilConsumed, CounterKind = BattleStatusCounterKind.Charges, IsPositive = true },
                    new() { Code = "resource", Name = "资源", Description = "绑定敌人", EffectType = "None",
                        MaxStacks = 10, Lifetime = BattleStatusLifetime.Encounter, IsPositive = true },
                    new() { Code = "poison", Name = "中毒", Description = "周期伤害", EffectType = "DamageOverTime",
                        SnapshotRefresh = refresh }
                ]
            }));
            return new(connection, db, new(db, catalog));
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
