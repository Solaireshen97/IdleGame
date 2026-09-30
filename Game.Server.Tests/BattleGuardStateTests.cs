using Game.Server.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Fact]
    public async Task StrongestGuardKeepsActualProviderAndSnapshotAcrossReloadAndEqualRefresh()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var ally = await test.AddSlotAsync(2, "Protected");
        var statuses = new BattleStatusService(test.Db, MonsterCombatTestFactory.CreateCatalog().Statuses);
        var guards = new BattleGuardService(statuses);
        Assert.True(await guards.ApplyAsync(test.Room, ally.Id, 10, new("Character", test.Character.Id, "first"), true));
        Assert.False(await guards.ApplyAsync(test.Room, ally.Id, 10, new("Character", ally.Id, "equal"), false));
        await test.Db.SaveChangesAsync();
        await using var fresh = test.CreateDbContext();
        var persisted = new BattleGuardService(new BattleStatusService(fresh, MonsterCombatTestFactory.CreateCatalog().Statuses));
        var defense = await persisted.DefenseAsync(test.Room, ally.Id);
        Assert.Equal(10, defense.ReductionPercent);
        Assert.Equal(test.Character.Id, defense.SourceCharacterId);
        Assert.True(defense.KnightCounterEligible);
        var displayed = await statuses.DescribeAsync(test.Room, "Character", ally.Id);
        var guard = Assert.Single(displayed, effect => effect.Code == BattleGuardService.GuardCode);
        Assert.Equal(10m, guard.MagnitudeSnapshot);
        Assert.Equal("本回合有效", guard.DurationText);
        Assert.Contains("10%", guard.Description);
        Assert.Equal("first", guard.SourceSkillCode);
        Assert.Null((await test.Db.BattleStatusEffects.SingleAsync(effect => effect.EffectCode == BattleGuardService.GuardCode)).PerTickValue);
    }

    [Fact]
    public async Task StrongerNonCounterGuardReplacesProviderAndRemovesFormerCounterPermission()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var ally = await test.AddSlotAsync(2, "Protected");
        var statuses = new BattleStatusService(test.Db, MonsterCombatTestFactory.CreateCatalog().Statuses);
        var guards = new BattleGuardService(statuses);
        await guards.ApplyAsync(test.Room, ally.Id, 10, new("Character", test.Character.Id, "knight"), true);
        Assert.True(await guards.ApplyAsync(test.Room, ally.Id, 90, new("Character", ally.Id, "stronger"), false));
        var guard = await guards.DefenseAsync(test.Room, ally.Id);
        Assert.Equal(75, guard.ReductionPercent);
        Assert.Equal(ally.Id, guard.SourceCharacterId);
        Assert.False(guard.KnightCounterEligible);
        await guards.RecordHitAsync(test.Room, ally.Id, test.Monster.Id);
        Assert.False(await guards.ConsumeCounterAsync(test.Room, test.Character.Id, test.Monster.Id));
    }

    [Fact]
    public async Task MultipleProtectedHitsProduceOneOwnedCounterAndEndRoundClearsGuardStates()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var ally = await test.AddSlotAsync(2, "Protected");
        var statuses = new BattleStatusService(test.Db, MonsterCombatTestFactory.CreateCatalog().Statuses);
        var guards = new BattleGuardService(statuses);
        await guards.ApplyAsync(test.Room, test.Character.Id, 40, new("Character", test.Character.Id, "barrier"), true);
        await guards.ApplyAsync(test.Room, ally.Id, 10, new("Character", test.Character.Id, "barrier"), true);
        await guards.RecordHitAsync(test.Room, test.Character.Id, test.Monster.Id);
        await guards.RecordHitAsync(test.Room, ally.Id, test.Monster.Id);
        await guards.RecordHitAsync(test.Room, ally.Id, test.Monster.Id);
        var ready = Assert.Single(await statuses.DescribeAsync(test.Room, "Character", test.Character.Id),
            effect => effect.Code == BattleGuardService.ReadyCode);
        Assert.Equal("剩余 1 次", ready.CounterText);
        Assert.Equal("barrier", ready.SourceSkillCode);
        Assert.Equal(test.Monster.Id, ready.BoundTargetId);
        Assert.False(await guards.ConsumeCounterAsync(test.Room, test.Character.Id, test.Monster.Id + 1));
        Assert.True(await guards.ConsumeCounterAsync(test.Room, test.Character.Id, test.Monster.Id));
        Assert.False(await guards.ConsumeCounterAsync(test.Room, test.Character.Id, test.Monster.Id));
        await statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, [], [], healingOnly: true);
        await test.Db.SaveChangesAsync();
        Assert.Empty(await test.Db.BattleStatusEffects.ToListAsync());
    }
}
