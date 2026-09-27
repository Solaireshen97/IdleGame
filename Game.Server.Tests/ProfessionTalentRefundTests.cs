using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class ProfessionTalentRefundTests
{
    [Theory]
    [InlineData("Gathering")]
    [InlineData("Alchemy")]
    public async Task RefundOneRankUpdatesOnlyTheSelectedProfessionAndCannotDuplicatePoints(string profession)
    {
        await using var test = await RefundContext.CreateAsync();
        await SpendAsync(test, profession, "root", "root");

        var (progress, error) = await test.Service.RefundAsync("token", profession, $"{profession}-root");

        Assert.Null(error);
        Assert.Equal(8, progress!.AvailableTalentPoints);
        Assert.Equal(1, progress.Nodes.Single(node => node.Code == $"{profession}-root").Rank);
        Assert.Equal(10, test.Catalog.EffectValue(await test.Db.CharacterProfessionTalents.ToListAsync(), profession, "ExtraYieldChancePercent"));
        var other = profession == "Gathering" ? "Alchemy" : "Gathering";
        Assert.Equal(9, (await test.Service.GetAsync("token", other)).Progress!.AvailableTalentPoints);
        Assert.Null((await test.Service.RefundAsync("token", profession, $"{profession}-root")).Error);
        Assert.Equal("TalentNotLearned", (await test.Service.RefundAsync("token", profession, $"{profession}-root")).Error);
        Assert.Equal(9, (await test.Service.GetAsync("token", profession)).Progress!.AvailableTalentPoints);
        Assert.Empty(await test.Db.CharacterProfessionTalents.ToListAsync());
    }

    [Theory]
    [InlineData("Gathering")]
    [InlineData("Alchemy")]
    public async Task RefundParentAllowsRanksAboveRequirementAndNamesDependentWhenBlocked(string profession)
    {
        await using var test = await RefundContext.CreateAsync();
        await SpendAsync(test, profession, "root", "root", "root", "child");
        Assert.Null((await test.Service.RefundAsync("token", profession, $"{profession}-root")).Error);

        var blocked = await test.Service.RefundAsync("token", profession, $"{profession}-root");

        Assert.Null(blocked.Progress);
        Assert.StartsWith("TalentRefundBlocked:", blocked.Error);
        Assert.Contains("进阶收获", blocked.Error);
        Assert.Contains("2 级", blocked.Error);
        Assert.Equal(6, (await test.Service.GetAsync("token", profession)).Progress!.AvailableTalentPoints);
        Assert.Null((await test.Service.RefundAsync("token", profession, $"{profession}-child")).Error);
        Assert.Null((await test.Service.RefundAsync("token", profession, $"{profession}-root")).Error);
        Assert.Null((await test.Service.RefundAsync("token", profession, $"{profession}-root")).Error);
        Assert.Equal(9, (await test.Service.GetAsync("token", profession)).Progress!.AvailableTalentPoints);
    }

    [Theory]
    [InlineData("Gathering", "Gathering")]
    [InlineData("Alchemy", "Production")]
    public async Task RefundLocksTheWorkingProfessionAndAllowsTheOtherOne(string profession, string activity)
    {
        await using var test = await RefundContext.CreateAsync();
        var other = profession == "Gathering" ? "Alchemy" : "Gathering";
        await SpendAsync(test, profession, "root");
        await SpendAsync(test, other, "root");
        test.Db.CharacterActivities.Add(new CharacterActivity { CharacterId = 1, Kind = activity, SourceId = 1, StartedAtUtc = DateTime.UtcNow });
        await test.Db.SaveChangesAsync();

        Assert.Equal("ProfessionTalentLocked", (await test.Service.RefundAsync("token", profession, $"{profession}-root")).Error);
        Assert.Equal(8, (await test.Service.GetAsync("token", profession)).Progress!.AvailableTalentPoints);
        Assert.Null((await test.Service.RefundAsync("token", other, $"{other}-root")).Error);
        Assert.Equal(9, (await test.Service.GetAsync("token", other)).Progress!.AvailableTalentPoints);
    }

    [Fact]
    public async Task RefundRejectsMissingAuthenticationAndForeignOrUnknownNodes()
    {
        await using var test = await RefundContext.CreateAsync();
        await SpendAsync(test, "Gathering", "root");
        Assert.Equal("Unauthorized", (await test.Service.RefundAsync(null, "Gathering", "Gathering-root")).Error);
        Assert.Equal("ProfessionNotFound", (await test.Service.RefundAsync("token", "Unknown", "Gathering-root")).Error);
        Assert.Equal("TalentNotFound", (await test.Service.RefundAsync("token", "Gathering", "Alchemy-root")).Error);
        Assert.Equal("TalentNotFound", (await test.Service.RefundAsync("token", "Gathering", "missing")).Error);
        Assert.Equal(8, test.Character.GatheringTalentPoints);
    }

    [Fact]
    public async Task RefundConcurrencyConflictRollsBackThenAllowsASafeRetry()
    {
        await using var test = await RefundContext.CreateAsync();
        await SpendAsync(test, "Alchemy", "root");
        await test.Db.Database.ExecuteSqlRawAsync("UPDATE Characters SET Version = Version + 1 WHERE Id = 1");

        Assert.Equal("ConcurrencyConflict", (await test.Service.RefundAsync("token", "Alchemy", "Alchemy-root")).Error);
        Assert.Equal(8, (await test.Db.Characters.AsNoTracking().SingleAsync()).AlchemyTalentPoints);
        Assert.Equal(1, (await test.Db.CharacterProfessionTalents.AsNoTracking().SingleAsync()).Rank);
        Assert.Null((await test.Service.RefundAsync("token", "Alchemy", "Alchemy-root")).Error);
        Assert.Equal(9, (await test.Service.GetAsync("token", "Alchemy")).Progress!.AvailableTalentPoints);
    }

    private static async Task SpendAsync(RefundContext test, string profession, params string[] codes)
    {
        foreach (var code in codes) Assert.Null((await test.Service.SpendAsync("token", profession, $"{profession}-{code}")).Error);
    }

    private sealed class RefundContext(SqliteConnection connection, GameDbContext db, Character character,
        ProfessionCatalog catalog, ProfessionService service) : IAsyncDisposable
    {
        public GameDbContext Db => db;
        public Character Character => character;
        public ProfessionCatalog Catalog => catalog;
        public ProfessionService Service => service;

        public static async Task<RefundContext> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var character = new Character { Id = 1, UserId = 1, Name = "Tester", ProfessionCode = "knight",
                GatheringLevel = 10, GatheringTalentPoints = 9, AlchemyLevel = 10, AlchemyTalentPoints = 9 };
            db.AddRange(character, new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 },
                new UserLoginSession { UserId = 1, Token = "token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();
            var nodes = new[] { "Gathering", "Alchemy" }.SelectMany(profession => new[]
            {
                new ProfessionTalentNodeOptions { Code = $"{profession}-root", ProfessionCode = profession,
                    Name = "基础收获", Description = "增加收获", EffectType = "ExtraYieldChancePercent", ValuePerRank = 10, MaxRank = 3 },
                new ProfessionTalentNodeOptions { Code = $"{profession}-child", ProfessionCode = profession,
                    Name = "进阶收获", Description = "增加收获", EffectType = "ExtraYieldChancePercent", ValuePerRank = 10,
                    Tier = 2, PrerequisiteCode = $"{profession}-root", PrerequisiteRank = 2 }
            }).ToList();
            var catalog = new ProfessionCatalog(Options.Create(new ProfessionProgressionOptions
                { ExperienceToNextLevel = Enumerable.Repeat(10, 9).ToList(), TalentNodes = nodes }));
            var users = new UserService(db, ProgressionTestFactory.Create(), SkillTestFactory.Create());
            return new RefundContext(connection, db, character, catalog, new ProfessionService(db, users, catalog));
        }

        public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
