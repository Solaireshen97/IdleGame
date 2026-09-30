using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    private readonly ITestOutputHelper _queryOutput;

    public BattleServiceTests(ITestOutputHelper queryOutput) => _queryOutput = queryOutput;

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 1)]
    [InlineData(1, 100)]
    [InlineData(5, 100)]
    public async Task RoomDetailQueryBaselineUsesFreshContext(int partySize, int rewardRunCount)
    {
        var measurement = await MeasureRoomDetailAsync(partySize, rewardRunCount);

        Assert.Equal(partySize, measurement.Detail.Slots.Count);
        Assert.All(measurement.Detail.Slots, slot =>
        {
            Assert.Equal(2, slot.Skills.Count(skill => skill.SkillCode is not null));
            Assert.Single(slot.StatusEffects);
        });
        Assert.Single(measurement.Detail.MonsterEffects);
        Assert.NotNull(measurement.Detail.Rewards);
        Assert.Equal(rewardRunCount, measurement.Detail.CumulativeRewards!.CompletedRuns);
        Assert.Equal(0, measurement.Commands.Writes);
        Assert.Equal(1, measurement.Commands.StatusSelects);
        Assert.Equal(3, measurement.Commands.RewardSelects);
        Assert.InRange(measurement.Commands.Selects, 1, 40);
        Assert.True(measurement.Commands.Selects > 0);
        Assert.True(measurement.JsonBytes > 0);
    }

    [Fact]
    public async Task RoomDetailQueryBaselineRecordsInitialIntentWriteSeparately()
    {
        var measurement = await MeasureRoomDetailAsync(1, 1, seedIntent: false);

        Assert.NotNull(measurement.Detail.MonsterIntent);
        Assert.Equal(1, measurement.Commands.Writes);
        Assert.Equal(1, measurement.Commands.StatusSelects);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task LightweightRoomDetailQueryBudgetDoesNotGrowWithRewardHistory(int partySize)
    {
        var small = await MeasureRoomDetailAsync(partySize, 1, includeRewardDetails: false);
        var large = await MeasureRoomDetailAsync(partySize, 100, includeRewardDetails: false);
        var full = await MeasureRoomDetailAsync(partySize, 100);

        foreach (var measurement in new[] { small, large })
        {
            Assert.Null(measurement.Detail.Rewards);
            Assert.Null(measurement.Detail.CumulativeRewards);
            Assert.Equal(0, measurement.Commands.RewardSelects);
            Assert.Equal(1, measurement.Commands.StatusSelects);
            Assert.Equal(0, measurement.Commands.Writes);
            Assert.InRange(measurement.Commands.Selects, 1, 36);
            Assert.Equal(partySize, measurement.Detail.Slots.Count);
            Assert.All(measurement.Detail.Slots, slot => Assert.Single(slot.StatusEffects));
        }
        Assert.Equal(small.Commands.Selects, large.Commands.Selects);
        // RunSequence has two extra digits; ServerTimeUtc precision may vary slightly.
        Assert.InRange(Math.Abs(large.JsonBytes - small.JsonBytes), 0, 32);
        Assert.True(large.JsonBytes * 4 < full.JsonBytes,
            "Large reward history must materially reduce the lightweight response payload.");
        Assert.Equal(JsonSerializer.Serialize(full.Detail.Slots), JsonSerializer.Serialize(large.Detail.Slots));
        Assert.Equal(JsonSerializer.Serialize(full.Detail.MonsterEffects), JsonSerializer.Serialize(large.Detail.MonsterEffects));
        Assert.Equal(JsonSerializer.Serialize(full.Detail.MonsterIntent), JsonSerializer.Serialize(large.Detail.MonsterIntent));
    }

    private async Task<RoomDetailMeasurement> MeasureRoomDetailAsync(int partySize, int rewardRunCount,
        bool seedIntent = true, bool includeRewardDetails = true)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var party = new List<Character> { test.Character };
        for (var slot = 2; slot <= partySize; slot++)
            party.Add(await test.AddSlotAsync(slot, $"Knight {slot}"));

        test.Room.RunSequence = rewardRunCount;
        test.Room.RoundNumber = 2;
        test.Room.Status = RoomStatus.Cooldown;
        foreach (var character in party)
        {
            character.ProfessionCode = "knight";
            await test.AddSkillAsync(character, 1, "knight-strike", autoUse: false);
            await test.AddSkillAsync(character, 2, "knight-guard", autoUse: false);
            test.Db.BattleStatusEffects.Add(new BattleStatusEffect
            {
                RoomId = test.Room.Id, RunSequence = rewardRunCount,
                TargetType = "Character", TargetId = character.Id, EffectCode = "baseline-poison",
                AppliedRound = 1, ExpiresAfterRound = 5, Lifetime = BattleStatusLifetime.Rounds
            });
        }
        test.Db.BattleStatusEffects.Add(new BattleStatusEffect
        {
            RoomId = test.Room.Id, RunSequence = rewardRunCount,
            TargetType = "Monster", TargetId = test.Monster.Id, EffectCode = "baseline-poison",
            AppliedRound = 1, ExpiresAfterRound = 5, Lifetime = BattleStatusLifetime.Rounds
        });
        if (seedIntent)
            test.Db.MonsterIntents.Add(new MonsterIntent
            {
                RoomId = test.Room.Id, RunSequence = rewardRunCount, RoundNumber = test.Room.RoundNumber,
                MonsterId = test.Monster.Id, TargetType = "Front", TargetCharacterId = test.Character.Id,
                CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });

        for (var sequence = 1; sequence <= rewardRunCount; sequence++)
        {
            test.Db.RewardRuns.Add(new RewardRun
            {
                RoomId = test.Room.Id, Sequence = sequence, Status = "Victory",
                SettledAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
            foreach (var character in party)
                foreach (var kind in new[] { "Gold", "Experience", "Material" })
                    test.Db.RewardEntries.Add(new RewardEntry
                    {
                        RoomId = test.Room.Id, Sequence = sequence, EventKey = "clear",
                        UserId = character.UserId, CharacterId = character.Id,
                        Kind = kind, Code = kind == "Material" ? $"baseline-material-{sequence}" : kind,
                        Quantity = 10
                    });
        }
        await test.Db.SaveChangesAsync();

        // Fixture tracking must never eliminate FindAsync reads in the measured request.
        var counter = new RoomDetailCommandCounter();
        var options = new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite(test.Db.Database.GetConnectionString()!)
            .AddInterceptors(counter).Options;
        await using var db = new GameDbContext(options);
        Assert.Empty(db.ChangeTracker.Entries());
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        var combat = new MonsterCombatService(db, new MonsterCombatCatalog(Options.Create(new MonsterCombatOptions
        {
            StatusEffects = [new BattleStatusOptions
            {
                Code = "baseline-poison", Name = "中毒", Description = "用于请求计量的负面状态。",
                EffectType = "DamageOverTime", ValuePerStack = 2, Lifetime = BattleStatusLifetime.Rounds
            }]
        })), characterSkills: skills);
        var rooms = new RoomService(db, new UserService(db, progression, skills), progression,
            ConsumableTestFactory.Create(), skills, RewardTestFactory.CreateService(db, progression),
            monsterCombatService: combat, soulImprintCatalog: SoulImprintTestFactory.Create());

        var timer = Stopwatch.StartNew();
        var detail = await rooms.GetRoomDetailAsync(test.Room.Id, test.Token, includeRewardDetails);
        timer.Stop();
        Assert.NotNull(detail);
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(detail, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length;
        _queryOutput.WriteLine(
            $"RoomDetail party={partySize}, rewardRuns={rewardRunCount}, rewardEntries={partySize * rewardRunCount * 3}, " +
            $"intent={(seedIntent ? "existing" : "missing")}, rewards={includeRewardDetails}, SELECT={counter.Selects}, " +
            $"statusSELECT={counter.StatusSelects}, rewardSELECT={counter.RewardSelects}, writes={counter.Writes}, " +
            $"jsonUTF8Bytes={jsonBytes}, elapsedMs={timer.Elapsed.TotalMilliseconds:F2}");
        return new(detail, counter, jsonBytes);
    }

    private sealed record RoomDetailMeasurement(RoomDetailResponse Detail, RoomDetailCommandCounter Commands, int JsonBytes);

    private sealed class RoomDetailCommandCounter : DbCommandInterceptor
    {
        public int Selects { get; private set; }
        public int StatusSelects { get; private set; }
        public int RewardSelects { get; private set; }
        public int Writes { get; private set; }

        private void Record(DbCommand command)
        {
            var sql = command.CommandText.TrimStart();
            if (sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                Selects++;
                if (sql.Contains("BattleStatusEffects", StringComparison.OrdinalIgnoreCase)) StatusSelects++;
                if (sql.Contains("RewardRuns", StringComparison.OrdinalIgnoreCase) ||
                    sql.Contains("RewardEntries", StringComparison.OrdinalIgnoreCase) ||
                    sql.Contains("RewardEvents", StringComparison.OrdinalIgnoreCase)) RewardSelects++;
            }
            else if (sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) ||
                sql.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) ||
                sql.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)) Writes++;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result) { Record(command); return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Record(command); return ValueTask.FromResult(result); }
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result) { Record(command); return result; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Record(command); return ValueTask.FromResult(result); }
        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result) { Record(command); return result; }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        { Record(command); return ValueTask.FromResult(result); }
    }
}
