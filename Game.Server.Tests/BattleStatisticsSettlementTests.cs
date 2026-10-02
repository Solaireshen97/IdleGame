using Game.Shared.Models;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Server.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Fact]
    public async Task StatisticsInstalledDuringLegacyRunBeginsCoverageAtNextSettlement()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        test.Room.RoundNumber = 4; test.Room.Status = RoomStatus.Preparing;
        await test.Db.SaveChangesAsync();
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token)).Error);
        await using var fresh = test.CreateDbContext();
        var run = await fresh.Set<BattleRunStatistics>().SingleAsync();
        Assert.Equal(5, run.CoverageStartRound); Assert.Equal(1L, run.RecordedRounds);
        Assert.Equal(5, run.LastAggregatedRound);
        var encounter = await fresh.Set<BattleEncounterStatistics>().SingleAsync();
        Assert.Equal(5, encounter.FirstRound); Assert.Equal(5, encounter.LastRound);
    }

    [Fact]
    public async Task StatisticsExpiredVictoryRetainsEntireRosterAfterClosingClearsSlots()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        var second = await test.AddSlotAsync(2, "teammate");
        test.Room.IsRepeatBattle = true; test.Room.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
        test.Room.Status = RoomStatus.Preparing; await test.Db.SaveChangesAsync();
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token)).Error);
        Assert.NotNull(test.Room.ClosedAtUtc);
        await using var fresh = test.CreateDbContext();
        Assert.All(await fresh.RoomSlots.ToListAsync(), s => Assert.Null(s.CharacterId));
        var actors = await fresh.Set<BattleActorStatistics>().ToListAsync();
        Assert.Equal(new[] { test.Character.Id, second.Id }.Order(), actors.Select(a => a.CharacterId).Order());
        Assert.All(actors, a => { Assert.Equal(1L, a.PresentRounds); Assert.False(string.IsNullOrEmpty(a.Name)); });
        Assert.Equal(50L, actors.Sum(a => a.DamageDealt));
        Assert.Equal("Victory", (await fresh.Set<BattleRunStatistics>().SingleAsync()).Outcome);
    }

    [Fact]
    public async Task StatisticsWriterReplayOfCommittedSettlementDoesNotAddMetricsOrRows()
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Room.RoundNumber = 1; test.Room.Version = 7;
        var batch = new BattleSettlementSnapshot
        {
            RoomId = 1, RunSequence = test.Room.RunSequence, RoundNumber = 1, SettlementVersion = 7,
            DungeonId = 1, SettledAtUtc = DateTime.UtcNow, Enemy = new(1, "Slime", 1, 1, false),
            Participants = [new(1, 1, "Knight", "knight", null, 1, 100, new())],
            Events = [new() { RoomId = 1, RunSequence = test.Room.RunSequence, RoundNumber = 1,
                SettlementVersion = 7, MonsterId = 1, Sequence = 1, Kind = BattleEventKind.Damage,
                Source = new("Character", 1), Target = new("Monster", 1), ActualAmount = 13,
                CalculatedAmount = 100, ActionKind = BattleActionKind.NormalAttack }]
        };
        Assert.True(await new BattleStatisticsWriter(test.Db).ApplyAsync(test.Room, batch));
        await test.Db.SaveChangesAsync();
        await using var fresh = test.CreateDbContext();
        var room = await fresh.Rooms.SingleAsync();
        Assert.False(await new BattleStatisticsWriter(fresh).ApplyAsync(room, batch));
        await fresh.SaveChangesAsync();
        Assert.Equal(1L, (await fresh.Set<BattleRunStatistics>().SingleAsync()).RecordedRounds);
        Assert.Equal(1L, (await fresh.Set<BattleEncounterStatistics>().SingleAsync()).RecordedRounds);
        var actor = await fresh.Set<BattleActorStatistics>().SingleAsync();
        Assert.Equal(13L, actor.DamageDealt); Assert.Equal(1L, actor.PresentRounds);
        Assert.Equal(13L, (await fresh.Set<BattleAbilityStatistics>().SingleAsync()).DamageDealt);
    }

    [Fact]
    public async Task StatisticsCommitAllFourTablesWithActualDamageAndSurvivePlaybackLoss()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        var history = new BattleLogStore();
        var result = await EventService(test.Db, history).StartPreparationAsync(1, test.Token);
        Assert.Null(result.Error); Assert.NotEmpty(history.GetEvents(1));
        await using var fresh = test.CreateDbContext();
        var run = await fresh.Set<BattleRunStatistics>().SingleAsync();
        var encounter = await fresh.Set<BattleEncounterStatistics>().SingleAsync();
        var actor = await fresh.Set<BattleActorStatistics>().SingleAsync();
        var ability = await fresh.Set<BattleAbilityStatistics>().SingleAsync();
        Assert.Equal(1L, run.RecordedRounds); Assert.Equal(1, run.LastAggregatedRound);
        Assert.Equal(test.Room.Version, run.LastSettlementVersion); Assert.Equal("Victory", run.Outcome);
        Assert.NotNull(run.EndedAtUtc); Assert.Equal(1L, encounter.RecordedRounds);
        Assert.Equal(50L, actor.DamageDealt); Assert.Equal(actor.DamageDealt, ability.DamageDealt);
        Assert.Equal(1L, actor.PresentRounds); Assert.Equal(1L, actor.AliveRounds);
        var restartedHistory = new BattleLogStore(); Assert.Empty(restartedHistory.GetEvents(1));
        Assert.Null((await EventService(fresh, restartedHistory).SyncRoomAsync(1)).Error);
        Assert.Equal(50L, (await fresh.Set<BattleActorStatistics>().SingleAsync()).DamageDealt);
        Assert.Equal(1L, (await fresh.Set<BattleRunStatistics>().SingleAsync()).RecordedRounds);
    }

    [Fact]
    public async Task StatisticsConcurrencyConflictDoesNotCommitAbandonedRound()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        var service = EventService(test.Db, new BattleLogStore());
        Assert.Null((await service.StartPreparationAsync(1, test.Token)).Error);
        Assert.Null((await service.StartPreparationAsync(1, test.Token, 1)).Error);
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1); await test.Db.SaveChangesAsync();
        await using var workerDb = test.CreateDbContext();
        await workerDb.Rooms.LoadAsync(); await workerDb.RoomSlots.LoadAsync();
        await workerDb.Characters.LoadAsync(); await workerDb.Monsters.LoadAsync();
        var worker = EventService(workerDb, new BattleLogStore());
        Assert.Null((await service.CancelPreparationAsync(1, test.Token, 1, 1)).Error);
        await using var before = test.CreateDbContext();
        var committedDamage = (await before.Set<BattleActorStatistics>().SingleAsync()).DamageDealt;
        var abandoned = await worker.SyncRoomAsync(1);
        Assert.Null(abandoned.Result); Assert.Equal("ConcurrencyConflict", abandoned.Error);
        await using var after = test.CreateDbContext();
        Assert.Equal(1L, (await after.Set<BattleRunStatistics>().SingleAsync()).RecordedRounds);
        Assert.Equal(1L, (await after.Set<BattleEncounterStatistics>().SingleAsync()).RecordedRounds);
        Assert.Equal(committedDamage, (await after.Set<BattleActorStatistics>().SingleAsync()).DamageDealt);
        Assert.Equal(committedDamage, (await after.Set<BattleAbilityStatistics>().SingleAsync()).DamageDealt);
    }

    [Fact]
    public async Task StatisticsFinalHitBelongsToDefeatedEnemyBeforeWaveSwitch()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        test.Monster.RoomId = 1; test.Monster.WaveNumber = 1; test.Monster.Position = 1;
        test.Room.CurrentWaveNumber = 1; test.Room.TotalWaveCount = 2;
        var next = new Monster { RoomId = 1, WaveNumber = 2, Position = 1, Name = "next", Hp = 60,
            MaxHp = 60, Attack = 1, Defense = 2 };
        test.Db.Monsters.Add(next); await test.Db.SaveChangesAsync();
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token)).Error);
        Assert.Equal(next.Id, test.Room.MonsterId);
        await using var fresh = test.CreateDbContext();
        var encounter = await fresh.Set<BattleEncounterStatistics>().SingleAsync();
        Assert.Equal(test.Monster.Id, encounter.MonsterId); Assert.Equal(1, encounter.WaveNumber);
        var actor = await fresh.Set<BattleActorStatistics>().SingleAsync();
        Assert.Equal(test.Monster.Id, actor.MonsterId); Assert.Equal(50L, actor.DamageDealt);
        Assert.Equal(test.Monster.Id, (await fresh.Set<BattleAbilityStatistics>().SingleAsync()).MonsterId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatisticsNewRunPreservesCompletedRunAndDoesNotDuplicateSync(bool repeat)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        test.Room.IsRepeatBattle = repeat; await test.Db.SaveChangesAsync();
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token)).Error);
        if (repeat)
        {
            test.Room.BattleEndedAtUtc = DateTime.UtcNow.AddSeconds(-Game.Shared.BattleRules.RepeatBattleDelaySeconds - 1);
            await test.Db.SaveChangesAsync();
            Assert.Null((await test.Service.SyncRoomAsync(1)).Error);
        }
        else
        {
            var reset = await test.Service.ResetBattleAsync(1, test.Token);
            Assert.True(reset.Success); Assert.Null(reset.Error);
        }
        Assert.Equal(2, test.Room.RunSequence);
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token)).Error);
        Assert.Null((await test.Service.SyncRoomAsync(1)).Error);
        await using var fresh = test.CreateDbContext();
        var runs = await fresh.Set<BattleRunStatistics>().OrderBy(r => r.RunSequence).ToListAsync();
        Assert.Equal(new[] { 1, 2 }, runs.Select(r => r.RunSequence));
        Assert.All(runs, r => { Assert.Equal(1L, r.RecordedRounds); Assert.Equal("Victory", r.Outcome); });
        var actors = await fresh.Set<BattleActorStatistics>().OrderBy(a => a.RunSequence).ToListAsync();
        Assert.Equal(2, actors.Count); Assert.All(actors, a => Assert.Equal(50L, a.DamageDealt));
    }
}
