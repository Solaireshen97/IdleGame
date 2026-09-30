using Game.Server.Configuration;
using Game.Client.Services;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Fact]
    public async Task EventSnapshotsPreserveTransientChargesPowerOwnershipAndConsumptionOrder()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var collector = new BattleEventCollector();
        var statuses = new BattleStatusService(test.Db, new BattleStatusCatalog(Options.Create(new MonsterCombatOptions
        { StatusEffects = [new() { Code = "charges", Name = "次数", Description = "施放时信息", EffectType = "None", MaxStacks = 3,
            CounterKind = BattleStatusCounterKind.Charges, Lifetime = BattleStatusLifetime.UntilConsumed, IsPositive = true, IsDispellable = false }] })), collector);
        var party = await UnifiedPartyAsync(test.Db); test.Room.RoundNumber = 6;
        using (collector.Begin(test.Room, test.Monster, party))
        {
            await statuses.SetCounterAsync(test.Room, "Character", 1, "charges", 3, new("Character", 1, "charge-skill"), "Monster", 1);
            await statuses.ConsumeAsync(test.Room, "Character", 1, "charges", 1);
            await statuses.ConsumeAsync(test.Room, "Character", 1, "charges");
            await new BattleGuardService(statuses).ApplyAsync(test.Room, 1, 30, new("Character", 1, "guard-skill"), false);
            await statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, party, [], healingOnly: true);
            test.Room.RoundNumber++; test.Room.Version = 9;
            var facts = collector.Snapshot(test.Room);
            Assert.Equal(new BattleStatusChange?[] { BattleStatusChange.Added, BattleStatusChange.Consumed, BattleStatusChange.Consumed, BattleStatusChange.Added, BattleStatusChange.Expired }, facts.Select(f => f.StatusChange));
            Assert.Equal(new int?[] { 3, 2, 0, 1, 0 }, facts.Select(f => f.CountAfter));
            Assert.Equal(3, facts[0].Status!.Stacks); Assert.Equal("剩余 3 次", facts[0].Status!.CounterText);
            Assert.Equal("charge-skill", facts[0].Status!.SourceSkillCode); Assert.Equal(1, facts[0].Status!.SourceActorId);
            Assert.Equal("Character", facts[0].Target.ActorType); Assert.Equal("Monster", facts[0].Status!.BoundTargetType);
            Assert.Equal("Slime", facts[0].Status!.BoundTargetName); Assert.Equal(30, facts[3].Status!.MagnitudeSnapshot);
            Assert.Equal(Enumerable.Range(1, 5), facts.Select(f => f.Sequence));
            Assert.All(facts, f => { Assert.Equal(7, f.RoundNumber); Assert.Equal(9, f.SettlementVersion); Assert.Equal(1, f.MonsterId); });
            Assert.Empty(await statuses.GetActiveAsync(test.Room, "Character", [1]));
        }
        Assert.False(collector.IsRecording); Assert.Empty(collector.Snapshot(test.Room));
    }

    [Fact]
    public async Task EventPeriodicDamageKeepsItsSourceSnapshotAndActualOverkillBeforeExpiry()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var statuses = new BattleStatusService(test.Db, MonsterCombatTestFactory.CreateCatalog().Statuses);
        await statuses.ApplyAsync(test.Room, "Monster", 1, "poison", 1, [], "Enemy", 9, new("Character", 1, "poison-skill"));
        await test.Db.SaveChangesAsync(); test.Room.RoundNumber = 1; test.Monster.Hp = 2;
        using var recording = statuses.Events.Begin(test.Room, test.Monster, await UnifiedPartyAsync(test.Db));
        await statuses.ResolveEndOfRoundAsync(test.Room, test.Monster, await UnifiedPartyAsync(test.Db), []);
        var events = statuses.Events.Snapshot(test.Room);
        Assert.Equal(new[] { BattleEventKind.Damage, BattleEventKind.Defeat, BattleEventKind.Status }, events.Select(e => e.Kind));
        var hit = events[0]; Assert.Equal(BattleActionKind.Periodic, hit.ActionKind); Assert.Equal(9, hit.CalculatedAmount); Assert.Equal(2, hit.ActualAmount);
        Assert.Equal(2, hit.HpBefore); Assert.Equal(0, hit.HpAfter); Assert.Null(hit.Element); Assert.Equal("poison-skill", hit.SkillCode);
        Assert.Equal(1, hit.Source!.ActorId); Assert.Equal(BattleStatusChange.Expired, events[2].StatusChange);
        Assert.Equal(9, events[2].Status!.PerTickValue);
    }

    [Fact]
    public async Task EventFeedbackPublishesOnlyCommittedRoundAndRoomReturnsSameIdentities()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var history = new BattleLogStore(); var service = EventService(test.Db, history);
        var (result, error) = await service.StartPreparationAsync(1, test.Token);
        Assert.Null(error); Assert.NotEmpty(result!.Events);
        Assert.Equal(new[] { BattleEventKind.Damage, BattleEventKind.Damage }, result.Events.Select(e => e.Kind));
        Assert.Equal(new[] { 15, 12 }, result.Events.Select(e => e.ActualAmount));
        Assert.All(result.Events, e => { Assert.Equal(1, e.RoundNumber); Assert.True(e.Id > 0); Assert.Equal(test.Room.Version, e.SettlementVersion); });
        var progression = ProgressionTestFactory.Create(); var skills = SkillTestFactory.Create();
        var rooms = new RoomService(test.Db, new UserService(test.Db, progression, skills), progression, ConsumableTestFactory.Create(), skills,
            RewardTestFactory.CreateService(test.Db, progression), battleLogStore: history);
        var detail = (await rooms.GetRoomDetailAsync(1, test.Token))!;
        Assert.Equal(result.Events, detail.BattleEvents); Assert.Equal(1, detail.MonsterId); Assert.Equal(test.Room.Version, detail.RoomVersion);
        Assert.Equal(history.Epoch, detail.BattleHistoryEpoch);
        Assert.Equal(history.Epoch, result.BattleHistoryEpoch);
        var before = history.GetSnapshot(1);
        await service.SyncRoomAsync(1); await service.ExecuteRoundAsync(1, test.Token);
        Assert.Equal(before.Events, history.GetEvents(1));
    }

    [Fact]
    public async Task RoomProjectionBeforePublicationCanRecoverTheCommittedRoundExactlyOnce()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var history = new BattleLogStore();
        var progression = ProgressionTestFactory.Create(); var skills = SkillTestFactory.Create();
        var rooms = new RoomService(test.Db, new UserService(test.Db, progression, skills), progression,
            ConsumableTestFactory.Create(), skills, RewardTestFactory.CreateService(test.Db, progression), battleLogStore: history);
        var before = (await rooms.GetRoomDetailAsync(1, test.Token))!;
        // Omit the publisher to control the exact commit/publication boundary without timing races.
        var result = (await EventService(test.Db, null).StartPreparationAsync(1, test.Token)).Result!;
        var committed = (await rooms.GetRoomDetailAsync(1, test.Token))!;
        Assert.Equal(before.RoundNumber + 1, committed.RoundNumber);
        Assert.Empty(committed.BattleEvents);
        var coordinator = new BattleFeedbackCoordinator();
        Assert.Null(coordinator.Observe(before, committed, true));
        history.Append(1, result.Logs, result.ServerTimeUtc, result.Events);
        var published = (await rooms.GetRoomDetailAsync(1, test.Token))!;
        Assert.Equal(committed.RoomVersion, published.RoomVersion);
        Assert.NotEmpty(published.BattleEvents);
        Assert.NotNull(coordinator.Observe(committed, published, true));
        Assert.Null(coordinator.Observe(published, published, true));
    }

    [Fact]
    public async Task EventConcurrencyFailureDoesNotPublishAbandonedDamageOrBuffs()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        var history = new BattleLogStore(); var service = EventService(test.Db, history);
        Assert.Null((await service.StartPreparationAsync(1, test.Token)).Error);
        Assert.Null((await service.StartPreparationAsync(1, test.Token, 1)).Error);
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1); await test.Db.SaveChangesAsync();
        await using var workerDb = test.CreateDbContext();
        await workerDb.Rooms.LoadAsync(); await workerDb.RoomSlots.LoadAsync(); await workerDb.Characters.LoadAsync(); await workerDb.Monsters.LoadAsync();
        var worker = EventService(workerDb, history);
        Assert.Null((await service.CancelPreparationAsync(1, test.Token, 1, 1)).Error);
        var committed = history.GetSnapshot(1);
        var (result, error) = await worker.SyncRoomAsync(1);
        Assert.Null(result); Assert.Equal("ConcurrencyConflict", error);
        Assert.Equal(committed.Events, history.GetEvents(1)); Assert.Equal(committed.Logs.Select(l => l.Id), history.Get(1).Select(l => l.Id));
        await using var fresh = test.CreateDbContext(); Assert.Equal(1, (await fresh.Rooms.SingleAsync()).RoundNumber);
    }

    [Fact]
    public async Task EventHealingPotionReportsActualHealthAndCannotReplayUnusedQuota()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 99, characterAttack: 1, monsterAttack: 1);
        await test.AddPotionAsync(test.Character, 1, autoUse: false);
        var service = EventService(test.Db, new BattleLogStore());
        Assert.True((await service.QueueConsumableAsync(new() { RoomId = 1, CharacterId = 1, ConsumableSlotIndex = 1,
            ExpectedRunSequence = 1, ExpectedRoundNumber = 0, IsQueued = true }, test.Token)).Success);
        var (result, error) = await service.StartPreparationAsync(1, test.Token);
        Assert.Null(error); var heal = Assert.Single(result!.Events, e => e.Kind == BattleEventKind.Heal);
        Assert.Equal(BattleActionKind.Consumable, heal.ActionKind); Assert.Equal(1, heal.ActualAmount); Assert.True(heal.CalculatedAmount > heal.ActualAmount);
        Assert.Equal(99, heal.HpBefore); Assert.Equal(100, heal.HpAfter); Assert.Equal("Character", heal.Source!.ActorType); Assert.Equal(1, heal.Target.ActorId);
    }

    [Fact]
    public async Task EventNativeMageEchoPublishesOwnedResourceConsumptionAndSingleReaction()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1, monsterDefense: 0);
        test.Character.ProfessionCode = "mage"; test.Character.Level = 30; test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync(); await test.AddSkillAsync(test.Character, 1, "mage-arcane-bolt", true);
        await test.AddSkillAsync(test.Character, 2, "mage-frost-bolt", true); await test.AddSkillAsync(test.Character, 3, "mage-scorch", true);
        var (service, combat) = CreateProductionSoulBattleService(test);
        await MageMechanics.AddDisorderAsync(combat.Statuses, test.Room, 1, 1, [], "Mage");
        await MageMechanics.AddDisorderAsync(combat.Statuses, test.Room, 1, 1, [], "Mage"); await test.Db.SaveChangesAsync();
        var (result, error) = await service.StartPreparationAsync(1, test.Token); Assert.Null(error);
        var consumed = Assert.Single(result!.Events, e => e.Status?.Mechanic == BattleStatusMechanic.MageDisorder && e.StatusChange == BattleStatusChange.Consumed);
        Assert.Equal("Character", consumed.Target.ActorType); Assert.Equal(1, consumed.Target.ActorId); Assert.Equal(3, consumed.CountBefore); Assert.Equal(0, consumed.CountAfter);
        Assert.Equal("Monster", consumed.Status!.BoundTargetType); Assert.Equal(1, consumed.Status.BoundTargetId);
        var reaction = Assert.Single(result.Events, e => e.Kind == BattleEventKind.Damage && e.ActionKind == BattleActionKind.Mechanic);
        Assert.Equal("Character", reaction.Source!.ActorType); Assert.Equal(1, reaction.Source.ActorId); Assert.Equal(30, reaction.ActualAmount);
        Assert.Contains(result.Events, e => e.Status?.Mechanic == BattleStatusMechanic.MageEchoUsed && e.StatusChange == BattleStatusChange.Expired);
    }

    [Fact]
    public async Task EventHistoricalEchoHasFormalOwnerPowerSnapshotAndPreservesEarlierRoundRule()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var options = new SkillOptions { TalentNodes = [new() { Code = "sword-disruption", ValuePerRank = 60 }] };
        var skills = new SkillCatalog(Options.Create(options));
        options.TalentNodes[0].ValuePerRank = 99;
        var statuses = new BattleStatusService(test.Db, MonsterCombatTestFactory.CreateCatalog().Statuses);
        var legacy = new LegacyBattleTalentService(test.Db, skills, statuses);
        using var recording = statuses.Events.Begin(test.Room, test.Monster, await UnifiedPartyAsync(test.Db));
        Assert.True(await legacy.GrantEchoAsync(test.Room, 1, "talent-intercept-echo", 3, "intercept-skill"));
        Assert.Equal(0, await legacy.ConsumeEchoAsync(test.Room, 1));
        var display = Assert.Single(await statuses.DescribeAsync(test.Room, "Character", 1));
        Assert.Equal(60, display.MagnitudeSnapshot); Assert.Contains("60%", display.Description); Assert.Equal("剩余 1 次", display.CounterText);
        Assert.Equal("intercept-skill", display.SourceSkillCode); Assert.Equal(BattleStatusMechanic.NormalAttackEcho, display.Mechanic);
        await test.Db.SaveChangesAsync(); test.Room.RoundNumber++;
        Assert.Equal(60, await legacy.ConsumeEchoAsync(test.Room, 1)); Assert.Equal(0, await legacy.ConsumeEchoAsync(test.Room, 1));
    }

    [Fact]
    public async Task EventNativeGuardCounterBelongsToProviderAfterEnemyHitAndPermissionConsumption()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 10, monsterAttack: 10, monsterDefense: 0);
        test.Character.ProfessionCode = "swordsman"; test.Character.Level = 30; test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync(); await test.AddSkillAsync(test.Character, 1, "knight-faith-barrier", true);
        var (service, _) = CreateProductionSoulBattleService(test);
        var (result, error) = await service.StartPreparationAsync(1, test.Token); Assert.Null(error);
        var counter = Assert.Single(result!.Events, e => e.Kind == BattleEventKind.Damage && e.ActionKind == BattleActionKind.Counter);
        Assert.Equal("Character", counter.Source!.ActorType); Assert.Equal(1, counter.Source.ActorId); Assert.Equal(1, counter.Source.SlotIndex);
        var hit = Assert.Single(result.Events, e => e.Kind == BattleEventKind.Damage && e.Source?.ActorType == "Monster");
        var permission = Assert.Single(result.Events, e => e.Status?.Mechanic == BattleStatusMechanic.GuardCounterPermission && e.StatusChange == BattleStatusChange.Consumed);
        var ready = Assert.Single(result.Events, e => e.Status?.Mechanic == BattleStatusMechanic.GuardCounterattack && e.StatusChange == BattleStatusChange.Consumed);
        Assert.True(hit.Sequence < permission.Sequence && permission.Sequence < ready.Sequence && ready.Sequence < counter.Sequence);
        Assert.Equal("Character", ready.Target.ActorType); Assert.Equal(1, ready.Target.ActorId);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task EventRunCompletionRemovesRemainingCharacterResourcesForVictoryOrDefeat(bool defeat)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: defeat ? 1 : 100,
            characterAttack: defeat ? 1 : 1000, monsterAttack: defeat ? 100 : 1);
        var combat = new MonsterCombatService(test.Db, MonsterCombatTestFactory.CreateCatalog());
        await combat.Statuses.ApplyAsync(test.Room, "Character", 1, "slime-shell", 5, [], "Player", source: new("Character", 1, "buff-skill"));
        await test.Db.SaveChangesAsync();
        var progression = ProgressionTestFactory.Create(); var skills = SkillTestFactory.Create();
        var service = new BattleService(test.Db, new UserService(test.Db, progression, skills), ConsumableTestFactory.Create(), skills,
            RewardTestFactory.CreateService(test.Db, progression), monsterCombatService: combat);
        var (result, error) = await service.StartPreparationAsync(1, test.Token); Assert.Null(error);
        Assert.Equal(RoomStatus.BattleOver, result!.RoomStatus); Assert.Empty(await test.Db.BattleStatusEffects.ToListAsync());
        Assert.Contains(result.Events, e => e.Status?.Code == "slime-shell" && e.StatusChange == BattleStatusChange.Removed && e.CountAfter == 0);
    }

    private static BattleService EventService(GameDbContext db, BattleLogStore? history)
    {
        var progression = ProgressionTestFactory.Create(); var skills = SkillTestFactory.Create();
        return new(db, new UserService(db, progression, skills), ConsumableTestFactory.Create(), skills,
            RewardTestFactory.CreateService(db, progression), battleLogStore: history, soulImprintCatalog: SoulImprintTestFactory.Create());
    }
}

public class BattleEventHistoryTests
{
    [Fact]
    public void EventHistoryRetainsWholeLongSettlementAndReturnsIndependentCollections()
    {
        var store = new BattleLogStore(); var actor = new BattleEventActor("Character", 1);
        var facts = Enumerable.Range(1, 110).Select(i => new BattleEventResponse
        { Kind = BattleEventKind.Status, Target = actor, Sequence = i, StatusChange = BattleStatusChange.Consumed, CountAfter = 0 }).ToList();
        store.Append(1, ["旧日志"], DateTime.UtcNow, [facts[0]]);
        var published = store.Append(1, ["新日志"], DateTime.UtcNow, facts);
        Assert.Equal(110, published.Count); Assert.Equal(110, store.GetEvents(1).Count);
        Assert.Equal(110, published.Select(e => e.Id).Distinct().Count());
        facts.Clear(); published.Clear(); var copy = store.GetSnapshot(1); copy.Events.Clear(); copy.Logs[0].Text = "修改";
        Assert.Equal(110, store.GetEvents(1).Count); Assert.Equal("旧日志", store.Get(1)[0].Text);
        store.Append(1, [], DateTime.UtcNow, [new() { Kind = BattleEventKind.Status, Target = actor }]); Assert.Equal(80, store.GetEvents(1).Count);
    }

    [Fact]
    public void EventHistoryReplaceClearAndRoomsHaveMonotonicUniqueEventIdentities()
    {
        var store = new BattleLogStore(); var fact = new BattleEventResponse { Kind = BattleEventKind.Damage, Target = new("Monster", 1) };
        var first = store.Append(1, ["first"], DateTime.UtcNow, [fact]).Single();
        var second = store.Replace(1, ["next run"], DateTime.UtcNow, [fact with { RunSequence = 2 }]).Single();
        var other = store.Append(2, [], DateTime.UtcNow, [fact]).Single();
        Assert.True(first.Id < second.Id && second.Id < other.Id); Assert.Equal(second, Assert.Single(store.GetEvents(1)));
        store.Clear(1); Assert.Empty(store.GetSnapshot(1).Logs); Assert.Empty(store.GetSnapshot(1).Events); Assert.Single(store.GetEvents(2));
    }
}
