using System.Text.Json;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public sealed class BattleStatisticsQueryTests
{
    [Fact]
    public async Task MigrationPreservesHistoricalIdsAndCascadesOnlyFromRoom()
    {
        await using var test = await Context.CreateAsync(migrate: true);
        Assert.False(test.Db.Database.HasPendingModelChanges());
        test.Db.BattleRunStatistics.Add(new() { RoomId = 1, RunSequence = 1 });
        test.Db.BattleEncounterStatistics.Add(new() { RoomId = 1, RunSequence = 1, MonsterId = 9999 });
        test.Db.BattleActorStatistics.Add(new() { RoomId = 1, RunSequence = 1, MonsterId = 9999, CharacterId = 9999 });
        test.Db.BattleAbilityStatistics.Add(new() { RoomId = 1, RunSequence = 1, MonsterId = 9999,
            CharacterId = 9999, ActionKind = BattleActionKind.Periodic, SourceCode = "legacy-dot" });
        await test.Db.SaveChangesAsync();
        test.Db.ChangeTracker.Clear();
        test.Db.Rooms.Remove((await test.Db.Rooms.FindAsync(1))!);
        await test.Db.SaveChangesAsync();
        Assert.Empty(await test.Db.BattleRunStatistics.ToListAsync());
        Assert.Empty(await test.Db.BattleEncounterStatistics.ToListAsync());
        Assert.Empty(await test.Db.BattleActorStatistics.ToListAsync());
        Assert.Empty(await test.Db.BattleAbilityStatistics.ToListAsync());
        await test.Db.GetService<IMigrator>().MigrateAsync("20261002020000_AddSoulImprintAutoCondition");
        await test.Db.Database.MigrateAsync();
        Assert.Empty(await test.Db.BattleRunStatistics.ToListAsync());
    }

    [Fact]
    public async Task DetailsKeepTeamDenominatorsAndHideOtherUsersConfigurations()
    {
        await using var test = await Context.CreateAsync();
        await test.SeedStatisticsAsync();
        var (response, error) = await test.Query.ReadAsync(1, "viewer-token", characterId: 101);
        Assert.Null(error);
        Assert.Equal(10, response!.RecordedRounds);
        Assert.Equal(1000, response.Totals.DamageDealt);
        var actor = Assert.Single(response.Actors);
        Assert.Equal(60m, actor.DamagePerRound);
        Assert.Equal(60m, actor.DamageSharePercent);
        Assert.Single(actor.Configurations);
        Assert.Equal(600, Assert.Single(actor.Abilities).DamageDealt);
        var (team, _) = await test.Query.ReadAsync(1, "viewer-token");
        Assert.All(team!.Actors, x => Assert.Empty(x.Abilities));
        Assert.Empty(team.Actors.Single(x => x.CharacterId == 202).Configurations);
        Assert.True(team.Actors.Single(x => x.CharacterId == 202).HasMixedConfigurations);
        var (enemy, _) = await test.Query.ReadAsync(1, null, monsterId: 22);
        Assert.Equal(6, enemy!.RecordedRounds);
        Assert.Equal(400, enemy.Totals.DamageDealt);
        Assert.Empty(enemy.Actors.Single().Configurations);
        Assert.Equal(2, enemy.Encounters.Count);
    }

    [Fact]
    public async Task RoomCoverageCountsMissingRunsAndNeverSumsActorRounds()
    {
        await using var test = await Context.CreateAsync();
        await test.SeedStatisticsAsync();
        var room = (await test.Db.Rooms.FindAsync(1))!;
        room.RunSequence = 3;
        room.RoundNumber = 0;
        room.Status = RoomStatus.NotStarted;
        await test.Db.SaveChangesAsync();
        var (response, _) = await test.Query.ReadAsync(1, null, "room");
        Assert.Equal("Partial", response!.Coverage);
        Assert.Equal(1, response.MissingRuns); // Run 2 missing; unstarted run 3 is not missing history.
        Assert.Equal(10, response.RecordedRounds);
        Assert.Empty(response.Encounters);
        var (current, _) = await test.Query.ReadAsync(1, null);
        Assert.Equal("NotStarted", current!.Coverage);
        room.RoundNumber = 7;
        await test.Db.SaveChangesAsync();
        var (unrecorded, _) = await test.Query.ReadAsync(1, null);
        Assert.Equal("Unrecorded", unrecorded!.Coverage);
        test.Db.BattleRunStatistics.Add(new() { RoomId = 1, RunSequence = 3, CoverageStartRound = 8,
            RecordedRounds = 2, LastAggregatedRound = 9 });
        await test.Db.SaveChangesAsync();
        var (partial, _) = await test.Query.ReadAsync(1, null);
        Assert.Equal("Partial", partial!.Coverage);
        Assert.Equal(8, partial.CoverageStartRound);
        Assert.Equal(2, partial.RecordedRounds);
        var (latest, _) = await test.Query.ReadAsync(1, null, "room");
        Assert.Equal(9, latest!.LastRoundNumber); // Earlier run ended at round 10.
    }

    [Fact]
    public async Task EmptyHeaderShowsCurrentRosterAndClosedEmptyIsNotMissingHistory()
    {
        await using var test = await Context.CreateAsync();
        var room = (await test.Db.Rooms.FindAsync(1))!;
        room.RoundNumber = 0;
        room.Status = RoomStatus.NotStarted;
        test.Db.Characters.Add(new Character { Id = 101, UserId = 1, Name = "Zero", Hp = 10, MaxHp = 10 });
        test.Db.RoomSlots.Add(new RoomSlot { RoomId = 1, CharacterId = 101, UserId = 1, SlotIndex = 1 });
        await test.Db.SaveChangesAsync();
        var (emptyRoom, _) = await test.Query.ReadAsync(1, null, "room");
        Assert.Equal("NotStarted", emptyRoom!.Coverage);
        Assert.Equal(0, emptyRoom.MissingRuns);
        test.Db.BattleRunStatistics.Add(new() { RoomId = 1, RunSequence = 1 });
        await test.Db.SaveChangesAsync();
        var (header, _) = await test.Query.ReadAsync(1, "viewer-token");
        Assert.Equal("NotStarted", header!.Coverage);
        Assert.Equal(101, Assert.Single(header.Actors).CharacterId);
        Assert.Null(header.Actors[0].DamagePerRound);
        room.ClosedAtUtc = DateTime.UtcNow;
        await test.Db.SaveChangesAsync();
        var (closed, _) = await test.Query.ReadAsync(1, "viewer-token");
        Assert.Equal("Complete", closed!.Coverage);
        Assert.Equal("NoBattle", closed.Outcome);
        Assert.Equal(0, closed.MissingRuns);
    }

    [Fact]
    public async Task AccessAndScopeValidationMatchRoomVisibility()
    {
        await using var test = await Context.CreateAsync();
        await test.SeedStatisticsAsync();
        Assert.Null((await test.Query.ReadAsync(1, null)).Error);
        var room = (await test.Db.Rooms.FindAsync(1))!;
        room.IsPublic = false;
        await test.Db.SaveChangesAsync();
        Assert.Equal("NotFound", (await test.Query.ReadAsync(1, null)).Error);
        Assert.Null((await test.Query.ReadAsync(1, "viewer-token")).Error);
        room.IsPublic = true;
        room.ClosedAtUtc = DateTime.UtcNow;
        await test.Db.SaveChangesAsync();
        Assert.Equal("NotFound", (await test.Query.ReadAsync(1, null)).Error);
        Assert.Equal("InvalidStatisticsScope", (await test.Query.ReadAsync(1, "viewer-token", "room", monsterId: 11)).Error);
        Assert.Equal("InvalidStatisticsScope", (await test.Query.ReadAsync(1, "viewer-token", monsterId: 999)).Error);
        Assert.Equal("InvalidStatisticsScope", (await test.Query.ReadAsync(1, "viewer-token", characterId: 999)).Error);
    }

    private sealed class Context(SqliteConnection connection, GameDbContext db) : IAsyncDisposable
    {
        public GameDbContext Db { get; } = db;
        public BattleStatisticsQuery Query => new(Db, new UserService(Db, ProgressionTestFactory.Create(), SkillTestFactory.Create()));
        public static async Task<Context> CreateAsync(bool migrate = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
            if (migrate) await db.Database.MigrateAsync(); else await db.Database.EnsureCreatedAsync();
            db.AddRange(new User { Id = 1, UserName = "viewer", PasswordHash = "unused" },
                new UserLoginSession { UserId = 1, Token = "viewer-token", ExpireAt = DateTime.UtcNow.AddDays(1) },
                new Room { Id = 1, OwnerUserId = 1, IsPublic = true, RunSequence = 1, RoundNumber = 10, Status = RoomStatus.Cooldown });
            await db.SaveChangesAsync();
            return new(connection, db);
        }
        public async Task SeedStatisticsAsync()
        {
            Db.BattleRunStatistics.Add(new() { RoomId = 1, RunSequence = 1, RecordedRounds = 10, LastAggregatedRound = 10 });
            Db.BattleEncounterStatistics.AddRange(new BattleEncounterStatistics { RoomId = 1, RunSequence = 1, MonsterId = 11,
                RecordedRounds = 4 }, new BattleEncounterStatistics { RoomId = 1, RunSequence = 1, MonsterId = 22, RecordedRounds = 6 });
            var own = JsonSerializer.Serialize(new[] { new BattleStatisticsConfiguration { ProfessionCode = "mage" } });
            var other = JsonSerializer.Serialize(new[] { new BattleStatisticsConfiguration { ProfessionCode = "knight" },
                new BattleStatisticsConfiguration { ProfessionCode = "swordsman" } });
            Db.BattleActorStatistics.AddRange(new BattleActorStatistics { RoomId = 1, RunSequence = 1, MonsterId = 11,
                CharacterId = 101, UserId = 1, Name = "Mage", DamageDealt = 600, PresentRounds = 4, ConfigurationsJson = own },
                new BattleActorStatistics { RoomId = 1, RunSequence = 1, MonsterId = 22,
                    CharacterId = 202, UserId = 2, Name = "Knight", DamageDealt = 400, PresentRounds = 6, ConfigurationsJson = other });
            Db.BattleAbilityStatistics.Add(new() { RoomId = 1, RunSequence = 1, MonsterId = 11, CharacterId = 101,
                ActionKind = BattleActionKind.Skill, SourceCode = "fire", DamageDealt = 600 });
            await Db.SaveChangesAsync();
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
