using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Enums;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed partial class BattleServiceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task WindLv4FixedCyclesResetGrowthAcrossReloadsAndKeepRewardsAndThunderOnTheirRounds(int fireActors)
    {
        await using var test = await BattleTestContext.CreateAsync();
        for (var i = 2; i <= Math.Max(1, fireActors); i++) await test.AddSlotAsync(i, $"Fire {i}");
        var rig = await FireRigAsync(test, WindCycleTestOptions());
        test.Room.RoundNumber = 60;
        await test.Db.SaveChangesAsync();
        var breaks = 0;
        var expiries = 0;
        var discharges = 0;
        int? lastBreak = null;
        for (var local = 1; local <= 35; local++)
        {
            await using var db = test.CreateDbContext();
            var room = await db.Rooms.SingleAsync();
            var monster = await db.Monsters.SingleAsync();
            var characters = await db.Characters.ToListAsync();
            var party = (await db.RoomSlots.OrderBy(s => s.SlotIndex).ToListAsync())
                .Select(s => new BattleParticipant(s, characters.Single(c => c.Id == s.CharacterId))).ToList();
            room.RoundNumber = 60 + local - 1;
            var statuses = new BattleStatusService(db, rig.Catalog.Statuses);
            var phases = new MonsterPhaseService(db, rig.Catalog, statuses);
            Assert.Equal(local - 1, await phases.EncounterSkillRoundAsync(room, monster));
            await phases.BeginRoundAsync(room, monster, []);
            await phases.BeginRoundAsync(room, monster, []);
            var state = db.BattleMonsterPhaseStates.Local.Single();
            var cycle = local < 6 ? 0 : (local - 6) / 10 + 1;
            var phaseRound = local < 6 ? 0 : (local - 6) % 10 + 1;
            Assert.Equal(cycle, state.ActivationCount);
            Assert.Equal(local < 6 ? 6 : 6 + cycle * 10, state.NextActivationRound);
            Assert.Equal(lastBreak is { } b && local > b && local <= b + 3 ? 120 : 100,
                await statuses.AmplifyDamageAsync(room, monster.Id, 100));
            if (phaseRound == 1)
            {
                Assert.Equal(4, await statuses.StacksAsync(room, "Monster", monster.Id, "wind-test-static"));
                Assert.Equal(0, await statuses.StacksAsync(room, "Monster", monster.Id, "wind-test-growth"));
                Assert.Null(await phases.PendingStaticThunderAsync(room, monster));
                Assert.Equal(room.RoundNumber + 3, state.ExpiresAfterRound);
            }
            var pending = await phases.PendingStaticThunderAsync(room, monster);
            var context = new BattleExecutionContext(room, monster, party, new Dictionary<int, ElementType>(),
                new Dictionary<int, OperationPotionBonuses>(), []);
            var previousBreaks = state.BreakCount;
            // Multi-hit attacks from one actor must never provide extra removal credit.
            for (var repeat = 0; repeat < 2; repeat++)
                foreach (var p in party.Take(fireActors))
                    await phases.ObserveDirectDamageAsync(context, ElementType.Fire, 1, p.Character.Id);
            if (state.BreakCount != previousBreaks) { breaks++; lastBreak = local; }
            if (pending is not null)
            {
                var release = await phases.CanReleaseStaticThunderAsync(room, monster);
                Assert.Equal(fireActors == 0, release);
                if (release) discharges++;
                await phases.FinishStaticThunderAsync(room, monster, [], release);
            }
            await statuses.ResolveEndOfRoundAsync(room, monster, party, []);
            await phases.EndRoundAsync(room, monster, []);
            await phases.EndRoundAsync(room, monster, []);
            if (phaseRound == 4 && fireActors is 1 or 2) expiries++;
            var counts = fireActors switch
            {
                0 => new[] { 5, 0, 0, 0 },
                1 => new[] { 5, 5, 4, 0 },
                2 => new[] { 5, 5, 3, 0 },
                3 => new[] { 4, 4, 1, 0 },
                _ => new[] { 0, 0, 0, 0 }
            };
            Assert.Equal(phaseRound is >= 1 and <= 4 ? counts[phaseRound - 1] : 0,
                await statuses.StacksAsync(room, "Monster", monster.Id, "wind-test-static"));
            Assert.Equal(breaks, state.BreakCount);
            Assert.Equal(expiries, state.ExpiryCount);
            Assert.Equal(fireActors == 0 ? phaseRound >= 2 ? cycle : Math.Max(0, cycle - 1) : 0, discharges);
            await db.SaveChangesAsync();
        }
        await using var finalDb = test.CreateDbContext();
        var final = await finalDb.BattleMonsterPhaseStates.SingleAsync();
        Assert.Equal((3, fireActors >= 3 ? 3 : 0, fireActors is 1 or 2 ? 3 : 0,
            fireActors == 3 ? 30 : fireActors == 4 ? 12 : fireActors * 12),
            (final.ActivationCount, final.BreakCount, final.ExpiryCount, final.LinkedHitCount));
    }

    [Fact]
    public void WindLv4RejectsMixedOrOverlappingSchedulesAndExportsPeriodicMetadata()
    {
        foreach (var fault in new Action<StaticFieldOptions>[]
        {
            f => f.TriggerHpPercent = 70, f => f.FirstActivationRound = 0,
            f => f.CycleRounds = 0, f => f.CycleRounds = 6, f => f.FirstActivationRound = 251
        })
        {
            var options = WindCycleTestOptions();
            fault(options.Profiles["fire-test"].StaticField!);
            Assert.Throws<InvalidOperationException>(() => new MonsterCombatCatalog(Options.Create(options)));
        }
        var catalog = new MonsterCombatCatalog(Options.Create(WindCycleTestOptions()));
        var restored = new MonsterCombatCatalog(Options.Create(catalog.ExportOptions()));
        var field = restored.FindProfile("fire-test")!.StaticField!;
        Assert.Null(field.TriggerHpPercent);
        Assert.Equal((6, 10, 4, 3), (field.FirstActivationRound, field.CycleRounds, field.InitialStacks, field.GrowthStacksPerRound));
        Assert.Equal(new[] { "静电领域", "雷暴共鸣", "风暴循环" }, restored.GetAddedMechanics("fire-test", 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindLv4OldFrozenSnapshotsWithoutScheduleFieldsRemainSingleHpTrigger(bool oldLv3)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test, oldLv3 ? WindThunderTestOptions() : WindFieldTestOptions());
        var depths = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions()));
        var rewards = RewardTestFactory.CreateCatalog();
        var originalRules = new DungeonRunRulesService(test.Db, rig.Catalog, rewards, PartyScalingCatalog.Default, depths);
        await originalRules.EnsureAsync(test.Room);
        var row = Assert.Single(test.Db.DungeonRunRuleSnapshots.Local);
        var json = JsonNode.Parse(row.DefinitionJson)!;
        var field = json["Combat"]!["Profiles"]!["fire-test"]!["StaticField"]!.AsObject();
        field.Remove("FirstActivationRound"); field.Remove("CycleRounds");
        row.DefinitionJson = json.ToJsonString();
        row.Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.DefinitionJson))).ToLowerInvariant();
        var revision = row.Revision;
        await test.Db.SaveChangesAsync();
        var live = new MonsterCombatCatalog(Options.Create(WindCycleTestOptions()));
        var rules = new DungeonRunRulesService(test.Db, live, rewards, PartyScalingCatalog.Default, depths);
        var statuses = new BattleStatusService(test.Db, live.Statuses, runRules: rules);
        var phases = new MonsterPhaseService(test.Db, live, statuses, rules);
        test.Monster.Hp = 7000;
        for (var local = 1; local <= 30; local++)
        {
            test.Room.RoundNumber = local - 1;
            await phases.BeginRoundAsync(test.Room, test.Monster, []);
            if (await phases.CanReleaseStaticThunderAsync(test.Room, test.Monster))
                await phases.FinishStaticThunderAsync(test.Room, test.Monster, [], true);
            await statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, [], []);
            await phases.EndRoundAsync(test.Room, test.Monster, []);
        }
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal(1, state.ActivationCount);
        Assert.Equal(oldLv3 ? 0 : 1, state.ExpiryCount);
        Assert.Equal(70, phases.StaticFieldDefinition(test.Room, test.Monster)!.TriggerHpPercent);
        Assert.Equal(0, phases.StaticFieldDefinition(test.Room, test.Monster)!.CycleRounds);
        Assert.Equal(revision, (await rules.EnsureAsync(test.Room)).Revision);
    }

    private static MonsterCombatOptions WindCycleTestOptions()
    {
        var options = WindThunderTestOptions();
        options.StatusEffects.Single(s => s.Code == "wind-test-static").InitialStacks = 4;
        var field = options.Profiles["fire-test"].StaticField!;
        field.TriggerHpPercent = null; field.FirstActivationRound = 6; field.CycleRounds = 10;
        field.InitialStacks = 4; field.GrowthStacksPerRound = 3;
        return options;
    }
}
