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

public partial class BattleServiceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task LightLv4FixedCyclesResetStrengthAndAmplificationAcrossReloads(int darkActors)
    {
        await using var test = await BattleTestContext.CreateAsync();
        for (var i = 2; i <= darkActors; i++) await test.AddSlotAsync(i, $"Dark {i}");
        var rig = await FireRigAsync(test, MirrorLv4Options());
        test.Room.RoundNumber = 60;
        await test.Db.SaveChangesAsync();
        int? lastBreak = null;
        for (var local = 1; local <= 35; local++)
        {
            await using var db = test.CreateDbContext();
            var room = await db.Rooms.SingleAsync(); var monster = await db.Monsters.SingleAsync();
            var characters = await db.Characters.ToListAsync();
            var party = (await db.RoomSlots.OrderBy(s => s.SlotIndex).ToListAsync())
                .Select(s => new BattleParticipant(s, characters.Single(c => c.Id == s.CharacterId))).ToList();
            room.RoundNumber = 60 + local - 1;
            var statuses = new BattleStatusService(db, rig.Catalog.Statuses);
            var phases = new MonsterPhaseService(db, rig.Catalog, statuses);
            Assert.Equal(local - 1, await phases.EncounterSkillRoundAsync(room, monster));
            await phases.BeginRoundAsync(room, monster, [], party);
            await phases.BeginRoundAsync(room, monster, [], party);
            var state = db.BattleMonsterPhaseStates.Local.Single();
            var cycle = local < 6 ? 0 : (local - 6) / 10 + 1;
            var phaseRound = local < 6 ? 0 : (local - 6) % 10 + 1;
            Assert.Equal(cycle, state.ActivationCount);
            Assert.Equal(local < 6 ? 6 : 6 + cycle * 10, state.NextActivationRound);
            Assert.Equal(lastBreak is { } b && local > b && local <= b + 3 ? 20m : 0m,
                await statuses.ModifierAsync(room, "Monster", monster.Id, "DamageTakenPercent"));
            if (phaseRound == 1)
            {
                Assert.Equal(6, await phases.ReflectionMirrorStacksAsync(room, monster));
                Assert.Equal((2m, 3m), await phases.ReflectionMirrorRatesAsync(room, monster));
                Assert.Equal(room.RoundNumber + 3, state.ExpiresAfterRound);
            }
            var strength = phaseRound is >= 1 and <= 4 ? Math.Max(0, 6 - (phaseRound - 1) * darkActors) : 0;
            Assert.Equal(strength, await phases.ReflectionMirrorStacksAsync(room, monster));
            var growth = strength > 0 ? phaseRound - 1 : 0;
            Assert.Equal((2m + .5m * growth, 3m + growth), await phases.ReflectionMirrorRatesAsync(room, monster));
            var context = new BattleExecutionContext(room, monster, party, new Dictionary<int, ElementType>(),
                new Dictionary<int, OperationPotionBonuses>(), []);
            var previousBreaks = state.BreakCount;
            foreach (var p in party.Take(darkActors))
            {
                await phases.ObserveDirectDamageAsync(context, ElementType.Dark, 1, p.Character.Id);
                await phases.ObserveDirectDamageAsync(context, ElementType.Dark, 1, p.Character.Id);
            }
            if (state.BreakCount != previousBreaks) lastBreak = local;
            await statuses.ResolveEndOfRoundAsync(room, monster, party, []);
            await phases.EndRoundAsync(room, monster, []);
            await phases.EndRoundAsync(room, monster, []);
            var remaining = phaseRound is >= 1 and < 4 ? Math.Max(0, 6 - phaseRound * darkActors) : 0;
            Assert.Equal(remaining, await phases.ReflectionMirrorStacksAsync(room, monster));
            Assert.Equal(remaining > 0 ? phaseRound : 0,
                await statuses.StacksAsync(room, "Monster", monster.Id, "mirror-amplification"));
            await db.SaveChangesAsync();
        }
        await using var finalDb = test.CreateDbContext();
        var final = await finalDb.BattleMonsterPhaseStates.SingleAsync();
        Assert.Equal((3, darkActors >= 2 ? 3 : 0, darkActors < 2 ? 3 : 0,
            darkActors >= 2 ? 18 : darkActors * 12),
            (final.ActivationCount, final.BreakCount, final.ExpiryCount, final.LinkedHitCount));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task LightLv4DispelClearsEachCycleAndLastRoundSuccessGetsFullReward(int windowRound)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test, MirrorLv4Options());
        int? lastBreak = null;
        for (var local = 1; local <= 35; local++)
        {
            test.Room.RoundNumber = local - 1;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            var phaseRound = local < 6 ? 0 : (local - 6) % 10 + 1;
            Assert.Equal(lastBreak is { } b && local > b && local <= b + 3 ? 20m : 0m,
                await rig.Statuses.ModifierAsync(test.Room, "Monster", test.Monster.Id, "DamageTakenPercent"));
            if (phaseRound == windowRound)
            {
                await rig.Phases.ObserveReflectionMirrorDispelAsync(rig.Context, "mirror-test");
                await rig.Phases.ObserveReflectionMirrorDispelAsync(rig.Context, "mirror-test");
                lastBreak = local;
                Assert.Equal(0, await rig.Phases.ReflectionMirrorStacksAsync(test.Room, test.Monster));
                Assert.Equal((2m, 3m), await rig.Phases.ReflectionMirrorRatesAsync(test.Room, test.Monster));
            }
            await MirrorEndAsync(test, rig);
        }
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal((3, 3, 0, 0, 36), (state.ActivationCount, state.BreakCount,
            state.ExpiryCount, state.LinkedHitCount, state.NextActivationRound));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LightLv4OldSnapshotsWithoutScheduleKeepSingleHpTrigger(bool oldLv3)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test, oldLv3 ? MirrorLv3Options() : MirrorTestOptions());
        var depths = new DungeonDepthCatalog(Options.Create(new DungeonDepthOptions()));
        var rewards = RewardTestFactory.CreateCatalog();
        var oldRules = new DungeonRunRulesService(test.Db, rig.Catalog, rewards, PartyScalingCatalog.Default, depths);
        await oldRules.EnsureAsync(test.Room);
        var row = Assert.Single(test.Db.DungeonRunRuleSnapshots.Local);
        var json = JsonNode.Parse(row.DefinitionJson)!;
        var mirror = json["Combat"]!["Profiles"]!["fire-test"]!["ReflectionMirror"]!.AsObject();
        mirror.Remove("FirstActivationRound"); mirror.Remove("CycleRounds");
        row.DefinitionJson = json.ToJsonString();
        row.Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.DefinitionJson))).ToLowerInvariant();
        var revision = row.Revision;
        await test.Db.SaveChangesAsync();
        var live = new MonsterCombatCatalog(Options.Create(MirrorLv4Options()));
        var rules = new DungeonRunRulesService(test.Db, live, rewards, PartyScalingCatalog.Default, depths);
        var statuses = new BattleStatusService(test.Db, live.Statuses, runRules: rules);
        var phases = new MonsterPhaseService(test.Db, live, statuses, rules);
        test.Monster.Hp = 7000;
        for (var local = 1; local <= 30; local++)
        {
            test.Room.RoundNumber = local - 1;
            await phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            await statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, rig.Party, []);
            await phases.EndRoundAsync(test.Room, test.Monster, []);
        }
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal((1, 1), (state.ActivationCount, state.ExpiryCount));
        Assert.Equal(70, phases.ReflectionMirrorDefinition(test.Room, test.Monster)!.TriggerHpPercent);
        Assert.Equal(0, phases.ReflectionMirrorDefinition(test.Room, test.Monster)!.CycleRounds);
        Assert.Equal(revision, (await rules.EnsureAsync(test.Room)).Revision);
    }

    [Fact]
    public async Task LightLv4BossDeathEndsCycleWithoutRewardAndNeverReactivates()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rig = await FireRigAsync(test, MirrorLv4Options());
        for (var local = 1; local <= 16; local++)
        {
            test.Room.RoundNumber = local - 1;
            await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
            if (local == 16) test.Monster.Hp = 0;
            await MirrorEndAsync(test, rig);
        }
        Assert.False(await rig.Statuses.HasAsync(test.Room, "Monster", test.Monster.Id, "mirror-test"));
        Assert.False(await rig.Statuses.HasAsync(test.Room, "Monster", test.Monster.Id, "mirror-amplification"));
        test.Room.RoundNumber = 25;
        await rig.Phases.BeginRoundAsync(test.Room, test.Monster, [], rig.Party);
        var state = Assert.Single(test.Db.BattleMonsterPhaseStates.Local);
        Assert.Equal((2, 0, 1), (state.ActivationCount, state.BreakCount, state.ExpiryCount));
        Assert.Null(state.RewardStartsAtRound);
    }

    [Fact]
    public void LightLv4RejectsMixedOrOverlappingSchedulesAndDeepCopiesPeriodicMetadata()
    {
        foreach (var fault in new Action<ReflectionMirrorOptions>[]
        {
            m => m.TriggerHpPercent = 70, m => m.FirstActivationRound = 0,
            m => m.CycleRounds = 0, m => m.CycleRounds = 6,
            m => m.FirstActivationRound = 251, m => m.CycleRounds = 251
        })
        {
            var options = MirrorLv4Options(); fault(options.Profiles["fire-test"].ReflectionMirror!);
            Assert.Throws<InvalidOperationException>(() => new MonsterCombatCatalog(Options.Create(options)));
        }
        var source = MirrorLv4Options(); var catalog = new MonsterCombatCatalog(Options.Create(source));
        var restored = new MonsterCombatCatalog(Options.Create(catalog.ExportOptions()));
        var mirror = restored.FindProfile("fire-test")!.ReflectionMirror!;
        Assert.Null(mirror.TriggerHpPercent);
        Assert.Equal((6, 10, 6, 3), (mirror.FirstActivationRound, mirror.CycleRounds, mirror.InitialStacks, mirror.GrowthRounds));
        Assert.Equal(new[] { "琉辉反镜", "Amplification", "黎明循环" }, restored.GetAddedMechanics("fire-test", 1));
        source.Profiles["fire-test"].ReflectionMirror!.CycleRounds = 8;
        Assert.Equal(10, catalog.FindProfile("fire-test")!.ReflectionMirror!.CycleRounds);
    }

    private static MonsterCombatOptions MirrorLv4Options()
    {
        var options = MirrorLv3Options(); var mirror = options.Profiles["fire-test"].ReflectionMirror!;
        mirror.TriggerHpPercent = null; mirror.FirstActivationRound = 6; mirror.CycleRounds = 10;
        mirror.InitialStacks = 6;
        mirror.MaxHpCapPercentPerStack = 3;
        var status = options.StatusEffects.Single(s => s.Code == "mirror-test");
        status.InitialStacks = status.MaxStacks = 6;
        return options;
    }
}
