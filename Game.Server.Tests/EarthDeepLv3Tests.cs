using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task EarthResonanceGrowsAfterActionsCapsAtThreeAndOnlyABreakClearsItAcrossReloads(int breakLocalRound)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test, EarthResonanceTestOptions());
        test.Room.RoundNumber = 60;
        test.Monster.Hp = 7000;
        await test.Db.SaveChangesAsync();
        for (var local = 1; local <= 9; local++)
        {
            await using var db = test.CreateDbContext();
            var room = await db.Rooms.SingleAsync();
            var monster = await db.Monsters.SingleAsync();
            room.RoundNumber = 60 + local - 1;
            var statuses = new BattleStatusService(db, rig.Catalog.Statuses);
            var phases = new MonsterPhaseService(db, rig.Catalog, statuses);
            await phases.BeginRoundAsync(room, monster, []);
            var expectedBefore = breakLocalRound > 0 && local > breakLocalRound ? 0 : Math.Min(3, local - 1);
            Assert.Equal(expectedBefore * 10m, await statuses.ModifierAsync(room, "Monster", monster.Id, "AttackPercent"));
            if (local == breakLocalRound)
            {
                var context = new BattleExecutionContext(room, monster, [], new Dictionary<int, ElementType>(),
                    new Dictionary<int, OperationPotionBonuses>(), []);
                await phases.ObserveDirectDamageAsync(context, ElementType.Wind, 400);
                Assert.Equal(0m, await statuses.ModifierAsync(room, "Monster", monster.Id, "ReductionPercent"));
                Assert.Equal(0m, await statuses.ModifierAsync(room, "Monster", monster.Id, "AttackPercent"));
            }
            Assert.Equal(breakLocalRound > 0 && local > breakLocalRound && local <= breakLocalRound + 3 ? 120 : 100,
                await statuses.AmplifyDamageAsync(room, monster.Id, 100));
            await statuses.ResolveEndOfRoundAsync(room, monster, [], []);
            await phases.EndRoundAsync(room, monster, []);
            await phases.EndRoundAsync(room, monster, []); // Duplicate end cannot add another stack.
            var expectedAfter = breakLocalRound > 0 && local >= breakLocalRound ? 0 : Math.Min(3, local);
            Assert.Equal(expectedAfter, await statuses.StacksAsync(room, "Monster", monster.Id, "earth-test-resonance"));
            if (local >= 5)
            {
                Assert.Equal(0m, await statuses.ModifierAsync(room, "Monster", monster.Id, "ReductionPercent"));
                var state = await db.BattleMonsterPhaseStates.SingleAsync();
                Assert.Equal(1, state.ActivationCount);
                Assert.Equal(breakLocalRound == 0 ? 1 : 0, state.ExpiryCount);
                Assert.Equal(breakLocalRound == 0 ? 0 : 1, state.BreakCount);
            }
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task EarthResonanceCannotBeDispelledAndDeadBossCannotGrowOrLeakItToTheNextEncounter()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test, EarthResonanceTestOptions());
        test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
        Assert.Null(await rig.Statuses.RemoveFirstAsync(test.Room, "Monster", [test.Monster.Id], true));
        Assert.Equal(1, await rig.Statuses.StacksAsync(test.Room, "Monster", test.Monster.Id, "earth-test-resonance"));
        test.Room.RoundNumber++;
        test.Monster.Hp = 0;
        await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
        Assert.Equal(1, await rig.Statuses.StacksAsync(test.Room, "Monster", test.Monster.Id, "earth-test-resonance"));
        await test.Db.SaveChangesAsync();
        await rig.Statuses.RemoveBoundToAsync(test.Room.Id, "Monster", test.Monster.Id);
        await test.Db.SaveChangesAsync();
        Assert.Equal(0, await rig.Statuses.StacksAsync(test.Room, "Monster", test.Monster.Id, "earth-test-resonance"));
        Assert.False((await test.Db.BattleMonsterPhaseStates.SingleAsync()).IsActive);
    }

    [Fact]
    public void EarthResonanceRejectsDispellableOrTemporaryOrHiddenAttackStacksAndExportsTheLink()
    {
        foreach (var fault in new Action<MonsterCombatOptions>[]
        {
            o => o.StatusEffects.Single(s => s.Code == "earth-test-resonance").IsDispellable = true,
            o => o.StatusEffects.Single(s => s.Code == "earth-test-resonance").Lifetime = BattleStatusLifetime.Rounds,
            o => o.StatusEffects.Single(s => s.Code == "earth-test-resonance").IsHidden = true,
            o => o.Profiles["fire-test"].EarthArmor!.ResonanceStatusCode = "earth-test-armor"
        })
        {
            var options = EarthResonanceTestOptions();fault(options);
            Assert.Throws<InvalidOperationException>(() => new MonsterCombatCatalog(Options.Create(options)));
        }
        var catalog = new MonsterCombatCatalog(Options.Create(EarthResonanceTestOptions()));
        var rebuilt = new MonsterCombatCatalog(Options.Create(catalog.ExportOptions()));
        Assert.Equal("earth-test-resonance", rebuilt.FindProfile("fire-test")!.EarthArmor!.ResonanceStatusCode);
        Assert.Equal(new[] { "矿脉护甲", "地脉共鸣" }, rebuilt.GetAddedMechanics("fire-test", 1));
    }

    private static MonsterCombatOptions EarthResonanceTestOptions()
    {
        var options = EarthTestOptions();
        options.StatusEffects.Add(new() { Code = "earth-test-resonance", Name = "地脉共鸣", Description = "Persistent attack stacks",
            EffectType = "AttackPercent", ValuePerStack = 10, MaxStacks = 3, Lifetime = BattleStatusLifetime.Encounter,
            IsPositive = true, IsDispellable = false });
        var armor = options.Profiles["fire-test"].EarthArmor!;
        armor.ResonanceStatusCode = "earth-test-resonance";armor.WindowRounds = 5;armor.BreakWindDamagePercent = 4;
        return options;
    }
}
