using Game.Server.Configuration;
using Game.Server.Services;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed partial class BattleServiceTests
{
    [Fact]
    public async Task WindLv3TwoFireActorsClearOnFourthRoundWithThreeFullRewardRounds()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var second = await test.AddSlotAsync(2, "Second Fire");
        var rig = await FireRigAsync(test, WindThunderTestOptions());
        test.Monster.Hp = 7000;
        for (var round = 0; round < 4; round++)
        {
            test.Room.RoundNumber = round;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
            for (var repeat = 0; repeat < 2; repeat++)
            {
                await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Fire, 1, test.Character.Id);
                await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Fire, 1, second.Id);
            }
            await rig.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, []);
            await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
            await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
            Assert.Equal(new[] {3, 3, 1, 0}[round], await rig.Statuses.StacksAsync(test.Room, "Monster", test.Monster.Id, "wind-test-static"));
            Assert.Null(await rig.Phases.PendingStaticThunderAsync(test.Room, test.Monster));
        }
        var phase = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal((1, 1, 0, 7), (phase.ActivationCount, phase.BreakCount, phase.ExpiryCount, phase.LinkedHitCount));
        for (var round = 4; round <= 7; round++)
        {
            test.Room.RoundNumber = round;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
            Assert.Equal(round <= 6 ? 120 : 100, await rig.Statuses.AmplifyDamageAsync(test.Room, test.Monster.Id, 100));
            await rig.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, []);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindLv3ThunderIsPreviewedThenReleasedOrCanceledBeforeEnemyAction(bool canceled)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 1000);
        await test.AddSlotAsync(2, "Other", hp: 1000);
        var rig = await FireRigAsync(test, WindThunderTestOptions());
        foreach (var p in rig.Party) p.Character.Hp = p.Character.MaxHp = 1000;
        test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        Assert.NotEqual("wind-test-thunder", (await rig.Monster.EnsureIntentAsync(test.Room, test.Monster)).SkillCode);
        test.Db.MonsterIntents.RemoveRange(test.Db.MonsterIntents.Local.ToList());
        await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
        await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
        Assert.Equal(5, await rig.Statuses.StacksAsync(test.Room, "Monster", test.Monster.Id, "wind-test-static"));
        test.Room.RoundNumber++;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        var preview = await rig.Monster.EnsureIntentAsync(test.Room, test.Monster);
        Assert.Equal("wind-test-thunder", preview.SkillCode);
        Assert.False(preview.IsInterrupted);
        Assert.False(await rig.Monster.InterruptCurrentIntentAsync(test.Room, test.Monster));
        if (canceled) await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Fire, 1, test.Character.Id);
        using var recording = rig.Statuses.Events.Begin(test.Room, test.Monster, rig.Party);
        await rig.Monster.ExecuteIntentAsync(test.Room, test.Monster, rig.Party, new Dictionary<int, ElementType>(), []);
        Assert.All(rig.Party, p => Assert.Equal(canceled ? 1000 : 850, p.Character.Hp));
        var phase = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal(canceled, phase.IsActive);
        Assert.Equal(0, phase.BreakCount);
        Assert.Equal(0, phase.ExpiryCount); // A thunder discharge is distinct from natural window expiry.
        Assert.Null(phase.RewardStartsAtRound);
        Assert.False(await rig.Statuses.HasAsync(test.Room, "Monster", test.Monster.Id, "wind-test-thunder-pending"));
        var hits = rig.Statuses.Events.Snapshot(test.Room).Where(e => e.Kind == BattleEventKind.Damage && e.SkillCode == "wind-test-thunder").ToList();
        Assert.Equal(canceled ? 0 : 2, hits.Count);
        await rig.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, []);
        await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
        test.Room.RoundNumber++;
        Assert.Equal(canceled ? "wind-test-thunder" : null, await rig.Phases.PendingStaticThunderAsync(test.Room, test.Monster));
    }

    [Fact]
    public async Task WindLv3OneFireActorCancelsThunderAndNaturallyFinishesWithoutReward()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 10000);
        var rig = await FireRigAsync(test, WindThunderTestOptions());
        test.Monster.Hp = 7000;
        for (var round = 0; round < 4; round++)
        {
            test.Room.RoundNumber = round;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
            var intent = await rig.Monster.EnsureIntentAsync(test.Room, test.Monster);
            Assert.Equal(round == 2, intent.SkillCode == "wind-test-thunder");
            await rig.Phases.ObserveDirectDamageAsync(rig.Context, ElementType.Fire, 1, test.Character.Id);
            await rig.Monster.ExecuteIntentAsync(test.Room, test.Monster, rig.Party, new Dictionary<int, ElementType>(), []);
            await rig.Statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, []);
            await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
            Assert.Equal(new[] {4, 5, 4, 0}[round], await rig.Statuses.StacksAsync(test.Room, "Monster", test.Monster.Id, "wind-test-static"));
        }
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal((1,0,1,4), (state.ActivationCount,state.BreakCount,state.ExpiryCount,state.LinkedHitCount));
        Assert.Null(state.RewardStartsAtRound);
    }

    [Fact]
    public async Task WindLv3PendingThunderAndIntentSurviveReloadWithoutDuplicatePreview()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 1000);
        var options = WindThunderTestOptions();
        var rig = await FireRigAsync(test, options);
        test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
        test.Room.RoundNumber++;
        var intent = await rig.Monster.EnsureIntentAsync(test.Room, test.Monster);
        await test.Db.SaveChangesAsync();
        await using var db = test.CreateDbContext();
        var room = await db.Rooms.SingleAsync();
        var monster = await db.Monsters.SingleAsync();
        var character = await db.Characters.SingleAsync();
        var slot = await db.RoomSlots.SingleAsync();
        var catalog = new MonsterCombatCatalog(Options.Create(options));
        var statuses = new BattleStatusService(db, catalog.Statuses);
        var phases = new MonsterPhaseService(db, catalog, statuses);
        var service = new MonsterCombatService(db, catalog, statuses: statuses, phases: phases);
        Assert.Equal(intent.Id, (await service.EnsureIntentAsync(room, monster)).Id);
        Assert.Equal("wind-test-thunder", (await service.GetIntentResponseAsync(room, monster))!.SkillCode);
        await phases.BeginRoundAsync(room, monster, []);
        await service.ExecuteIntentAsync(room, monster, [new(slot, character)], new Dictionary<int, ElementType>(), []);
        await db.SaveChangesAsync();
        Assert.False((await db.BattleMonsterPhaseStates.SingleAsync()).IsActive);
        Assert.Empty(await db.BattleStatusEffects.Where(s => s.EffectCode == "wind-test-thunder-pending").ToListAsync());
        Assert.Equal(1, (await db.BattleMonsterPhaseStates.SingleAsync()).ActivationCount);
    }

    [Fact]
    public async Task WindLv3DeathClearsPendingThunderWithoutRewardOrDischarge()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test, WindThunderTestOptions());
        test.Monster.Hp = 7000;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, []);
        await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
        test.Monster.Hp = 0;
        test.Room.RoundNumber++;
        await rig.Phases.EndRoundAsync(test.Room, test.Monster, []);
        Assert.Null(await rig.Phases.PendingStaticThunderAsync(test.Room, test.Monster));
        Assert.False(await rig.Statuses.HasAsync(test.Room, "Monster", test.Monster.Id, "wind-test-thunder-pending"));
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal((false,0,0), (state.IsActive,state.BreakCount,state.ExpiryCount));
        Assert.Null(state.RewardStartsAtRound);
    }

    [Fact]
    public void WindLv3CatalogClonesThunderAndRejectsIncompleteLinkage()
    {
        var options = WindThunderTestOptions();
        var catalog = new MonsterCombatCatalog(Options.Create(options));
        var copy = catalog.ExportOptions();
        Assert.Equal(5, new MonsterCombatCatalog(Options.Create(copy)).FindProfile("fire-test")!.StaticField!.ThunderAtStacks);
        copy.Profiles["fire-test"].StaticField!.ThunderAtStacks = 4;
        Assert.Equal(5, catalog.FindProfile("fire-test")!.StaticField!.ThunderAtStacks);
        Assert.Throws<InvalidOperationException>(() => new MonsterCombatCatalog(Options.Create(copy)));
        options.Profiles["fire-test"].StaticField!.ThunderPendingStatusCode = "wind-test-hit";
        Assert.Throws<InvalidOperationException>(() => new MonsterCombatCatalog(Options.Create(options)));
        options = WindThunderTestOptions();
        options.Skills.Single(s => s.Code == "wind-test-thunder").IsInterruptible = true;
        Assert.Throws<InvalidOperationException>(() => new MonsterCombatCatalog(Options.Create(options)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindLv3LiveChangesPreserveFrozenThunderAndOldSnapshotsWithoutThunderFields(bool originallyLv3)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test, originallyLv3 ? WindThunderTestOptions() : WindFieldTestOptions());
        test.Monster.Hp = 7000;
        var depths = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions()));
        var rewards = RewardTestFactory.CreateCatalog();
        var originalRules = new DungeonRunRulesService(test.Db, rig.Catalog, rewards, PartyScalingCatalog.Default, depths);
        await originalRules.EnsureAsync(test.Room);
        if (!originallyLv3)
        {
            var row = Assert.Single(test.Db.DungeonRunRuleSnapshots.Local);
            var json = JsonNode.Parse(row.DefinitionJson)!;
            var field = json["Combat"]!["Profiles"]!["fire-test"]!["StaticField"]!.AsObject();
            field.Remove("ThunderAtStacks");field.Remove("ThunderSkillCode");field.Remove("ThunderPendingStatusCode");
            row.DefinitionJson = json.ToJsonString();
            row.Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.DefinitionJson))).ToLowerInvariant();
        }
        await test.Db.SaveChangesAsync();
        var live = WindThunderTestOptions();
        live.Skills.Single(s => s.Code == "wind-test-thunder").DamagePowerPercent = 210;
        var catalog = new MonsterCombatCatalog(Options.Create(live));
        var rules = new DungeonRunRulesService(test.Db, catalog, rewards, PartyScalingCatalog.Default, depths);
        var statuses = new BattleStatusService(test.Db, catalog.Statuses, runRules: rules);
        var phases = new MonsterPhaseService(test.Db, catalog, statuses, rules);
        await phases.BeginRoundAsync(test.Room, test.Monster, []);
        Assert.Equal(originallyLv3 ? 5 : 0, phases.StaticFieldDefinition(test.Room, test.Monster)!.ThunderAtStacks);
        await phases.EndRoundAsync(test.Room, test.Monster, []);
        test.Room.RoundNumber++;
        Assert.Equal(originallyLv3 ? "wind-test-thunder" : null, await phases.PendingStaticThunderAsync(test.Room, test.Monster));
        Assert.Equal(originallyLv3 ? 150 : (int?)null, rules.CombatFor(test.Room).FindSkill("wind-test-thunder")?.DamagePowerPercent);
    }

    private static MonsterCombatOptions WindThunderTestOptions()
    {
        var options = WindFieldTestOptions();
        options.StatusEffects.Single(s => s.Code == "wind-test-static").InitialStacks = 3;
        options.StatusEffects.Add(new() { Code="wind-test-thunder-pending", Name="Preview", Description="Preview", EffectType="None",
            IsPositive=true, IsDispellable=false, Lifetime=BattleStatusLifetime.Rounds });
        options.Skills.Add(new() { Code="wind-test-thunder",Name="Thunder",Description="Thunder",TargetType="AllAlive",
            DamagePowerPercent=150, IsInterruptible=false, DangerLevel="Deadly" });
        options.Profiles["fire-test"].Skills.Add(new() {Code="wind-test-thunder"});
        var field = options.Profiles["fire-test"].StaticField!;
        field.InitialStacks=3;field.GrowthStacksPerRound=2;field.ThunderAtStacks=5;
        field.ThunderSkillCode="wind-test-thunder";field.ThunderPendingStatusCode="wind-test-thunder-pending";
        return options;
    }
}
