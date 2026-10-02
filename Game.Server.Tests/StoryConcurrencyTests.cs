using System.Text.Json;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Story;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public sealed class StoryConcurrencyTests
{
    [Fact]
    public async Task IndependentClientsClaimOnlyOneRewardAndReplayKeysCannotChangeQuest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"story-race-{Guid.NewGuid():N}.db");
        GameDbContext Open() => new(new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False;Default Timeout=5").Options);
        try
        {
            await using (var seed = Open())
            {
                await seed.Database.EnsureCreatedAsync();
                var definition = StoryQuestCatalog.Default.FindQuest("ch01-02")!;
                // Include authorization in this snapshot to check the entire atomic turn-in bundle.
                var snapshot = JsonSerializer.Deserialize<Game.Server.Configuration.StoryQuestDefinition>(JsonSerializer.Serialize(definition))!;
                snapshot.UnlockMapNodeCodes = ["northshire-wolves"];
                seed.AddRange(new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 },
                    new Character { Id = 1, UserId = 1, Name = "Hero", Level = 1 },
                    new UserLoginSession { UserId = 1, Token = "token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) },
                    new UserStoryState { UserId = 1, TutorialCharacterId = 1, CurrentQuestCode = "ch01-02" },
                    new StoryQuestProgress { UserId = 1, QuestCode = "ch01-02", ActorCharacterId = 1,
                        Status = "ReadyToTurnIn", Progress = 1, ActivatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                        DefinitionJson = JsonSerializer.Serialize(snapshot) });
                await seed.SaveChangesAsync();
            }
            using var gate = new Barrier(2);
            async Task<(string? Error, string RequestId)> ClientAsync()
            {
                await using var db = Open();
                var service = Service(db);
                var view = (await service.GetAsync("token")).Response!;
                var requestId = Guid.NewGuid().ToString("N");
                Assert.Equal("ch01-02", view.CurrentQuest!.Code);
                Assert.True(gate.SignalAndWait(TimeSpan.FromSeconds(10)));
                var result = await service.TurnInAsync("token", "ch01-02", new()
                    { RequestId = requestId, ExpectedVersion = view.CurrentQuest.Version });
                return (result.Error, requestId);
            }
            var results = await Task.WhenAll(Task.Run(ClientAsync), Task.Run(ClientAsync));
            Assert.Contains(results, x => x.Error is null);
            Assert.All(results, x => Assert.True(x.Error is null or "ConcurrencyConflict", x.Error));
            await using var verify = Open();
            var service = Service(verify);
            foreach (var result in results)
                Assert.Null((await service.TurnInAsync("token", "ch01-02", new()
                    { RequestId = result.RequestId, ExpectedVersion = 0 })).Error);
            Assert.Equal(20, (await verify.Characters.SingleAsync()).Gold);
            Assert.Equal(2, (await verify.CharacterItemStacks.SingleAsync()).Quantity);
            Assert.Single(await verify.StoryMapUnlocks.ToListAsync());
            Assert.Single(await verify.StoryQuestProgress.Where(x => x.QuestCode == "ch01-03").ToListAsync());
            Assert.Single(await verify.StoryActionReceipts.Where(x => x.QuestCode == "ch01-02").ToListAsync());
            var alias = Guid.NewGuid().ToString("N");
            Assert.Null((await service.TurnInAsync("token", "ch01-02", new() { RequestId = alias })).Error);
            Assert.Null((await verify.StoryActionReceipts.SingleAsync(x => x.RequestId == alias)).QuestCode);
            Assert.Equal("RequestIdConflict", (await service.TurnInAsync("token", "ch01-03", new() { RequestId = alias })).Error);
            Assert.Equal(20, (await verify.Characters.SingleAsync()).Gold);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task EventsBeforeActivationAreIgnoredAndSameAccountSameBattleCountsOnce()
    {
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        var definition = JsonSerializer.Deserialize<Game.Server.Configuration.StoryQuestDefinition>(
            JsonSerializer.Serialize(StoryQuestCatalog.Default.FindQuest("ch01-07")))!;
        definition.RequiredCount = 2;
        db.AddRange(new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 },
            new Character { Id = 1, UserId = 1, Name = "One" }, new Character { Id = 2, UserId = 1, Name = "Two" },
            new UserStoryState { UserId = 1, TutorialCharacterId = 1, CurrentQuestCode = definition.Code },
            new StoryQuestProgress { UserId = 1, QuestCode = definition.Code, ActivatedAtUtc = now,
                DefinitionJson = JsonSerializer.Serialize(definition) });
        await db.SaveChangesAsync();
        await StoryProgressService.RecordAsync(db, 1, "DungeonClear", definition.TargetCode, "old-battle", 1, now.AddTicks(-1));
        Assert.Empty(db.StoryEventReceipts.Local);
        await StoryProgressService.RecordAsync(db, 1, "DungeonClear", definition.TargetCode, "battle:1:1:clear", 1, now);
        await StoryProgressService.RecordAsync(db, 2, "DungeonClear", definition.TargetCode, "battle:1:1:clear", 1, now);
        await db.SaveChangesAsync();
        Assert.Equal(1, (await db.StoryQuestProgress.SingleAsync()).Progress);
        Assert.Single(await db.StoryEventReceipts.ToListAsync());
        db.ChangeTracker.Clear();
        await StoryProgressService.RecordAsync(db, 2, "DungeonClear", definition.TargetCode, "battle:1:1:clear", 1, now);
        await db.SaveChangesAsync();
        Assert.Equal(1, (await db.StoryQuestProgress.SingleAsync()).Progress);
    }

    private static StoryService Service(GameDbContext db) => new(db,
        new UserService(db, ProgressionTestFactory.Create(), SkillTestFactory.Create()), StoryQuestCatalog.Default);
}
