using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    private const string Lv4Cold = "water-deep-lv4-cold", Lv4Warm = "water-deep-lv4-warm",
        Lv4Freeze = "water-deep-lv4-freeze";

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task WaterLv4CyclesUseBossLocalClockAndResetPersonalRewardAndFreeze(int earthActors)
    {
        await using var test = await BattleTestContext.CreateAsync();
        for (var i = 2; i <= 5; i++) await test.AddSlotAsync(i, "队友" + i);
        var rig = await ColdRigAsync(test, "water-deep-lv4-boss");
        const int encounterStart = 40;
        for (var local = 1; local <= 33; local++)
        {
            test.Room.RoundNumber = encounterStart + local - 1;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            var cycleRound = local < 6 ? -1 : (local - 6) % 10;
            Assert.Equal(earthActors == 0 && cycleRound == 1,
                await rig.Statuses.HasAsync(test.Room, "Character", test.Character.Id, Lv4Freeze));
            Assert.Equal(earthActors == 3 && cycleRound is >= 2 and <= 4 ? 20m : 0m,
                await rig.Statuses.ModifierAsync(test.Room, "Character", test.Character.Id, "DamageDealtPercent"));
            if (!await rig.Statuses.IsActionBlockedAsync(test.Room, test.Character.Id))
                for (var actor = 0; actor < earthActors; actor++)
                {
                    await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Earth, 10, rig.Party[actor].Character.Id);
                    await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Earth, 10, rig.Party[actor].Character.Id);
                }
            await ColdEndAsync(test, rig);
            await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
            var expectedStacks = cycleRound is < 0 or >= 3 ? 0 :
                earthActors == 0 ? 5 : earthActors == 2 ? 4 : cycleRound == 0 ? 3 : 0;
            Assert.Equal(expectedStacks, await rig.Statuses.StacksAsync(test.Room, "Character", test.Character.Id, Lv4Cold));
        }
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal(3, state.ActivationCount);
        Assert.Equal(3, state.ExpiryCount);
        Assert.Equal(earthActors == 3 ? 15 : 0, state.BreakCount);
        Assert.Equal(36, state.NextActivationRound);
        Assert.False(state.IsActive);
    }

    [Fact]
    public async Task WaterLv4PeriodicClockAndFreezeResetSurviveServiceAndDatabaseReload()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await ColdRigAsync(test, "water-deep-lv4-boss");
        await test.Db.SaveChangesAsync();
        for (var local = 1; local <= 29; local++)
        {
            await using var db = test.CreateDbContext();
            var room = await db.Rooms.SingleAsync();
            var monster = await db.Monsters.SingleAsync();
            var character = await db.Characters.SingleAsync();
            room.RoundNumber = local + 99;
            var statuses = new BattleStatusService(db, rig.Catalog.Statuses);
            var phases = new MonsterPhaseService(db, rig.Catalog, statuses);
            await phases.BeginRoundAsync(room, monster, []);
            Assert.Equal(local is 7 or 17 or 27,
                await statuses.HasAsync(room, "Character", character.Id, Lv4Freeze));
            await statuses.ResolveEndOfRoundAsync(room, monster,
                [new(await db.RoomSlots.SingleAsync(), character)], []);
            await phases.EndRoundAsync(room, monster, []);
            await db.SaveChangesAsync();
        }
        await using var finalDb = test.CreateDbContext();
        var state = await finalDb.BattleMonsterPhaseStates.SingleAsync();
        Assert.Equal(3, state.ActivationCount);
        Assert.Equal(3, state.ExpiryCount);
        Assert.Equal(36, state.NextActivationRound);
    }

    [Fact]
    public async Task WaterLv4LastRoundCleanseGrantsFullRewardAndNextCycleCanRewardAgain()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await ColdRigAsync(test, "water-deep-lv4-boss");
        var cast = new BattleCastExecution(rig.Context, ColdCharacterSkill(BattleEffectKind.Cleanse),
            BattleActor.ForCharacter(rig.Party[0]));
        for (var local = 1; local <= 22; local++)
        {
            test.Room.RoundNumber = local - 1;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            if (local is 9 or 19) Assert.True((await rig.Effects.ExecuteAsync(cast)).CleansingOccurred);
            Assert.Equal(local is >= 10 and <= 12 or >= 20 and <= 22,
                await rig.Statuses.HasAsync(test.Room, "Character", test.Character.Id, Lv4Warm));
            await ColdEndAsync(test, rig);
        }
        Assert.Equal(2, Assert.Single(test.Db.BattleMonsterPhaseStates.Local).BreakCount);
    }

    [Fact]
    public void WaterLv4ValidatesPeriodicDeclarationsAndKeepsFrozenOneShotOptionsCompatible()
    {
        foreach (var fault in new Action<DeepColdOptions>[]
        {
            c => c.TriggerHpPercent = 70, c => c.FirstActivationRound = 0,
            c => c.CycleRounds = 7, c => c.CycleRounds = 0
        })
        {
            var options = WaterLv1Catalog().ExportOptions();
            fault(options.Profiles["water-deep-lv4-boss"].DeepCold!);
            Assert.Throws<InvalidOperationException>(() => new MonsterCombatCatalog(Options.Create(options)));
        }
        var old = System.Text.Json.JsonSerializer.Deserialize<DeepColdOptions>("{\"TriggerHpPercent\":70,\"WindowRounds\":4}")!;
        Assert.Equal(70, old.TriggerHpPercent);
        Assert.Equal(ElementType.Fire, old.RemovalElement);
        Assert.Equal(0, old.FirstActivationRound);
        Assert.Equal(0, old.CycleRounds);
        var restored = new MonsterCombatCatalog(Options.Create(WaterLv1Catalog().ExportOptions()));
        var profile = restored.FindProfile(restored.ResolveDepthProfile("water-deep-lv1-monster-5", 10))!;
        Assert.Equal(6, profile.DeepCold!.FirstActivationRound);
        Assert.Equal(10, profile.DeepCold.CycleRounds);
        Assert.Equal(2, profile.DeepCold.GrowthStacksPerRound);
        Assert.Equal(ElementType.Earth, profile.DeepCold.RemovalElement);
        Assert.Equal(25, ElementMatchupRules.PlayerAttackPercent(profile.DeepCold.RemovalElement, ElementType.Water));
        Assert.DoesNotContain(profile.Skills, s => s.Code.StartsWith("depth-placeholder"));
        Assert.Contains("永冻循环", restored.GetAddedMechanics("water-deep-lv1-monster-5", 4));
    }

    [Fact]
    public async Task WaterFrozenDeclarationsWithoutRemovalElementRetainOriginalFireHandling()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await ColdRigAsync(test);
        var json = System.Text.Json.Nodes.JsonNode.Parse(
            System.Text.Json.JsonSerializer.Serialize(rig.Catalog.ExportOptions()))!;
        json["Profiles"]!["water-deep-lv2-boss"]!["DeepCold"]!.AsObject().Remove("RemovalElement");
        var oldOptions = System.Text.Json.JsonSerializer.Deserialize<MonsterCombatOptions>(json.ToJsonString())!;
        var oldCatalog = new MonsterCombatCatalog(Options.Create(oldOptions));
        var phases = new MonsterPhaseService(test.Db, oldCatalog, rig.Statuses);
        test.Monster.Hp = 7000;
        await phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        await phases.ObserveDirectDamageAsync(rig.Context, ElementType.Earth, 100, test.Character.Id);
        Assert.Equal(3, await rig.Statuses.StacksAsync(test.Room, "Character", test.Character.Id, DeepColdCode));
        await phases.ObserveDirectDamageAsync(rig.Context, ElementType.Fire, 100, test.Character.Id);
        Assert.Equal(2, await rig.Statuses.StacksAsync(test.Room, "Character", test.Character.Id, DeepColdCode));
    }
}
