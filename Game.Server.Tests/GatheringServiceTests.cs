using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Gathering;
using Game.Shared.Models;
using Game.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class GatheringServiceTests
{
    [Fact]
    public async Task GatheringTaskLocksOnlyGatheringTalentChangesUntilItStops()
    {
        await using var test = await GatheringTestContext.CreateAsync();
        test.First.GatheringLevel = 2;
        test.First.GatheringTalentPoints = 1;
        test.First.AlchemyLevel = 2;
        test.First.AlchemyTalentPoints = 1;
        await test.Db.SaveChangesAsync();
        var catalog = ProfessionTestFactory.Create();
        var gathering = test.NewService(catalog);
        var talents = new ProfessionService(test.Db,
            new UserService(test.Db, ProgressionTestFactory.Create(), SkillTestFactory.Create()), catalog);
        var started = await gathering.StartAsync(test.Token,
            new Game.Shared.Dtos.Gathering.StartGatheringRequest
            {
                CharacterId = test.First.Id, PointCode = "elwynn-peacebloom"
            });
        Assert.Null(started.Error);
        var view = await talents.GetAsync(test.Token, ProfessionCatalog.GatheringCode);
        Assert.True(view.Progress!.IsTalentLocked);
        Assert.All(view.Progress.Nodes, node => Assert.False(node.CanPurchase));
        Assert.Equal("ProfessionTalentLocked", (await talents.SpendAsync(test.Token,
            ProfessionCatalog.GatheringCode, "gather-yield")).Error);
        Assert.Equal("ProfessionTalentLocked", (await talents.ResetAsync(test.Token,
            ProfessionCatalog.GatheringCode)).Error);
        Assert.Null((await talents.SpendAsync(test.Token,
            ProfessionCatalog.AlchemyCode, "alchemy-save")).Error);
        Assert.Null((await gathering.StopAsync(test.Token, started.Response!.ActiveTask!.Id)).Error);
        Assert.False((await talents.GetAsync(test.Token, ProfessionCatalog.GatheringCode)).Progress!.IsTalentLocked);
        Assert.Null((await talents.SpendAsync(test.Token,
            ProfessionCatalog.GatheringCode, "gather-yield")).Error);
    }

    [Fact]
    public async Task ProfessionExperienceAndPurchasedTalentEffectsBelongToCharacter()
    {
        await using var test = await GatheringTestContext.CreateAsync();
        var catalog = ProfessionTestFactory.Create();
        var service = test.NewService(catalog);
        var first = await service.StartAsync(test.Token,
            new Game.Shared.Dtos.Gathering.StartGatheringRequest
            {
                CharacterId = test.First.Id, PointCode = "elwynn-peacebloom"
            });
        Assert.Null(first.Error);
        var initial = first.Response!.ActiveTask!;
        Assert.Null(await service.AdvanceDueAsync(initial.Id, initial.StartedAtUtc.AddSeconds(40)));
        Assert.Equal(2, test.First.GatheringLevel);
        Assert.Equal(1, test.First.GatheringTalentPoints);
        Assert.Equal(1, test.Second.GatheringLevel);
        Assert.Null((await service.StopAsync(test.Token, initial.Id)).Error);

        var talents = new ProfessionService(test.Db,
            new UserService(test.Db, ProgressionTestFactory.Create(), SkillTestFactory.Create()), catalog);
        Assert.Equal("ProfessionLevelTooLow", (await talents.SpendAsync(test.Token,
            ProfessionCatalog.GatheringCode, "gather-pace")).Error);
        var purchased = await talents.SpendAsync(test.Token, ProfessionCatalog.GatheringCode, "gather-yield");
        Assert.Null(purchased.Error);
        Assert.Equal(1, purchased.Progress!.Nodes.Single(node => node.Code == "gather-yield").Rank);
        Assert.Equal(0, test.First.GatheringTalentPoints);
        Assert.Equal(0, test.Second.GatheringTalentPoints);

        var second = await service.StartAsync(test.Token,
            new Game.Shared.Dtos.Gathering.StartGatheringRequest
            {
                CharacterId = test.First.Id, PointCode = "elwynn-peacebloom"
            });
        Assert.Null(second.Error);
        var enhanced = second.Response!.ActiveTask!;
        Assert.Equal(100, (await test.Db.GatheringTasks.FindAsync(enhanced.Id))!.ExtraYieldChancePercent);
        Assert.Equal("ProfessionTalentLocked", (await talents.ResetAsync(test.Token, ProfessionCatalog.GatheringCode)).Error);
        Assert.Null(await service.AdvanceDueAsync(enhanced.Id, enhanced.NextCycleAtUtc));
        Assert.Equal(2, (await test.Db.GatheringTasks.FindAsync(enhanced.Id))!.TotalQuantity);
        Assert.Equal(1, (await test.Db.GatheringTasks.FindAsync(enhanced.Id))!.ExtraYieldQuantity);
        Assert.Null((await service.StopAsync(test.Token, enhanced.Id)).Error);
        Assert.Null((await talents.ResetAsync(test.Token, ProfessionCatalog.GatheringCode)).Error);
        Assert.Equal(1, test.First.GatheringTalentPoints);
    }

    [Fact]
    public async Task RareGatheringBonusUsesOneOpportunityAndReportsBothOutputs()
    {
        await using var test = await GatheringTestContext.CreateAsync();
        test.First.GatheringLevel = 2;
        test.First.GatheringTalentPoints = 1;
        test.Db.AddRange(
            new CharacterBattleMilestone
            {
                CharacterId = test.First.Id, Kind = BattleMilestoneService.MonsterKillKind,
                TargetCode = "elwynn-grizzled-bear", Count = 1,
                FirstAtUtc = DateTime.UtcNow, LastAtUtc = DateTime.UtcNow
            },
            new CharacterGatheringOpportunity
            {
                CharacterId = test.First.Id, PointCode = "elwynn-earthroot",
                AvailableCount = 1, EarnedCount = 1
            });
        await test.Db.SaveChangesAsync();
        var catalog = ProfessionTestFactory.Create();
        var talents = new ProfessionService(test.Db,
            new UserService(test.Db, ProgressionTestFactory.Create(), SkillTestFactory.Create()), catalog);
        Assert.Null((await talents.SpendAsync(test.Token, ProfessionCatalog.GatheringCode, "gather-rare")).Error);
        var service = test.NewService(catalog);
        var started = await service.StartAsync(test.Token,
            new Game.Shared.Dtos.Gathering.StartGatheringRequest
            {
                CharacterId = test.First.Id, PointCode = "elwynn-earthroot"
            });
        Assert.Null(started.Error);
        Assert.Null(await service.AdvanceDueAsync(started.Response!.ActiveTask!.Id,
            started.Response.ActiveTask.NextCycleAtUtc));
        var result = (await service.GetAsync(test.Token)).Response!;
        Assert.Null(result.ActiveTask);
        Assert.Equal(1, result.RecentTasks.Single().TotalQuantity);
        Assert.Equal(1, result.RecentTasks.Single().BonusQuantity);
        Assert.Equal("宁神花", result.RecentTasks.Single().BonusMaterialName);
        Assert.Equal(0, (await test.Db.CharacterGatheringOpportunities.SingleAsync()).AvailableCount);
        Assert.Equal(1, (await test.Db.CharacterItemStacks.SingleAsync(item => item.ItemCode == "earthroot")).Quantity);
        Assert.Equal(1, (await test.Db.CharacterItemStacks.SingleAsync(item => item.ItemCode == "peacebloom")).Quantity);
    }

    [Fact]
    public async Task UnlocksAndGatheredItemsBelongToEachCharacter()
    {
        await using var test = await GatheringTestContext.CreateAsync();
        var (firstView, firstError) = await test.Service.GetAsync(test.Token);
        Assert.Null(firstError);
        Assert.True(firstView!.Points.Single(point => point.Code == "elwynn-peacebloom").IsUnlocked);
        Assert.False(firstView.Points.Single(point => point.Code == "elwynn-silverleaf").IsUnlocked);

        var (started, startError) = await test.Service.StartAsync(test.Token,
            new StartGatheringRequest { CharacterId = test.First.Id, PointCode = "elwynn-peacebloom" });
        Assert.Null(startError);
        var task = started!.ActiveTask!;
        Assert.Equal(test.First.Id, (await test.Db.CharacterActivities.SingleAsync()).CharacterId);
        Assert.Equal("Gathering", (await test.Db.CharacterActivities.SingleAsync()).Kind);
        Assert.True(await CharacterActivityManager.IsBusyAsync(test.Db, test.First.Id));
        var (_, busyError) = await test.Service.StartAsync(test.Token,
            new StartGatheringRequest { CharacterId = test.First.Id, PointCode = "elwynn-peacebloom" });
        Assert.Equal("CharacterBusy", busyError);

        test.Owner.ActiveCharacterId = test.Second.Id;
        test.Owner.Version++;
        await test.Db.SaveChangesAsync();
        var (_, lockedError) = await test.Service.StartAsync(test.Token,
            new StartGatheringRequest { CharacterId = test.Second.Id, PointCode = "elwynn-peacebloom" });
        Assert.Equal("PointLocked", lockedError);
        test.Db.CharacterBattleMilestones.Add(new CharacterBattleMilestone
        {
            CharacterId = test.Second.Id, Kind = BattleMilestoneService.MonsterKillKind,
            TargetCode = "tirisfal-dusk-bat", Count = 1,
            FirstAtUtc = DateTime.UtcNow, LastAtUtc = DateTime.UtcNow
        });
        await test.Db.SaveChangesAsync();
        var (secondStarted, secondError) = await test.Service.StartAsync(test.Token,
            new StartGatheringRequest { CharacterId = test.Second.Id, PointCode = "elwynn-peacebloom" });
        Assert.Null(secondError);
        Assert.NotNull(secondStarted!.ActiveTask);
        Assert.Equal(2, await test.Db.CharacterActivities.CountAsync());

        Assert.Null(await test.Service.AdvanceDueAsync(task.Id, task.StartedAtUtc.AddSeconds(65)));
        Assert.Equal((3, 3), (await test.Db.GatheringTasks.FindAsync(task.Id)) is { } advanced
            ? (advanced.CompletedCycles, advanced.TotalQuantity) : (0, 0));
        Assert.Equal(3, (await test.Db.CharacterItemStacks.SingleAsync(item =>
            item.CharacterId == test.First.Id && item.ItemCode == "peacebloom")).Quantity);
        var (secondView, _) = await test.Service.GetAsync(test.Token);
        Assert.Equal(0, secondView!.Points.Single(point => point.Code == "elwynn-peacebloom").CharacterQuantity);
    }

    [Fact]
    public async Task TwelveHourLimitAndRepeatedScanDoNotDuplicateHarvest()
    {
        await using var test = await GatheringTestContext.CreateAsync();
        var (view, error) = await test.Service.StartAsync(test.Token,
            new StartGatheringRequest { CharacterId = test.First.Id, PointCode = "elwynn-peacebloom" });
        Assert.Null(error);
        var task = view!.ActiveTask!;
        Assert.Equal(TimeSpan.FromHours(12), task.EndsAtUtc - task.StartedAtUtc);

        Assert.Null(await test.Service.AdvanceDueAsync(task.Id, task.EndsAtUtc.AddHours(1)));
        Assert.Null(await test.Service.AdvanceDueAsync(task.Id, task.EndsAtUtc.AddHours(2)));
        var finished = await test.Db.GatheringTasks.SingleAsync();
        Assert.Equal("Completed", finished.Status);
        Assert.Equal(2160, finished.CompletedCycles);
        Assert.Equal(2160, finished.TotalQuantity);
        Assert.Equal(2160, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Empty(await test.Db.CharacterActivities.ToListAsync());
    }

    [Fact]
    public async Task DeadlineFinishesTheCurrentConfiguredCycle()
    {
        await using var test = await GatheringTestContext.CreateAsync(cycleSeconds: 23);
        var (view, error) = await test.Service.StartAsync(test.Token,
            new StartGatheringRequest { CharacterId = test.First.Id, PointCode = "elwynn-peacebloom" });
        Assert.Null(error);
        var task = view!.ActiveTask!;
        Assert.Equal(TimeSpan.FromSeconds(43_217), task.EndsAtUtc - task.StartedAtUtc);

        Assert.Null(await test.Service.AdvanceDueAsync(task.Id, task.StartedAtUtc.AddHours(12)));
        Assert.Equal("Running", (await test.Db.GatheringTasks.SingleAsync()).Status);
        Assert.Null(await test.Service.AdvanceDueAsync(task.Id, task.EndsAtUtc));
        Assert.Equal("Completed", (await test.Db.GatheringTasks.SingleAsync()).Status);
        Assert.Equal(1879, (await test.Db.GatheringTasks.SingleAsync()).CompletedCycles);
    }

    [Fact]
    public async Task StoppingAfterCompletedRoundsKeepsOnlyThoseRewards()
    {
        await using var test = await GatheringTestContext.CreateAsync();
        var (view, _) = await test.Service.StartAsync(test.Token,
            new StartGatheringRequest { CharacterId = test.First.Id, PointCode = "elwynn-peacebloom" });
        var task = view!.ActiveTask!;
        Assert.Null(await test.Service.AdvanceDueAsync(task.Id, task.StartedAtUtc.AddSeconds(45)));

        var (stopped, stopError) = await test.Service.StopAsync(test.Token, task.Id);
        Assert.Null(stopError);
        Assert.Null(stopped!.ActiveTask);
        Assert.Equal(2, stopped.RecentTasks.Single().TotalQuantity);
        Assert.Empty(await test.Db.CharacterActivities.ToListAsync());
        Assert.Null(await test.Service.AdvanceDueAsync(task.Id, task.EndsAtUtc.AddHours(1)));
        Assert.Equal(2, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
    }

    [Fact]
    public async Task RareHarvestOnlySpendsAnOpportunityOnCompletion()
    {
        await using var test = await GatheringTestContext.CreateAsync();
        test.Db.AddRange(
            new CharacterBattleMilestone
            {
                CharacterId = test.First.Id, Kind = BattleMilestoneService.MonsterKillKind,
                TargetCode = "elwynn-grizzled-bear", Count = 1,
                FirstAtUtc = DateTime.UtcNow, LastAtUtc = DateTime.UtcNow
            },
            new CharacterGatheringOpportunity
            {
                CharacterId = test.First.Id, PointCode = "elwynn-earthroot",
                AvailableCount = 1, EarnedCount = 1
            });
        await test.Db.SaveChangesAsync();

        var (overview, _) = await test.Service.GetAsync(test.Token);
        var point = overview!.Points.Single(item => item.Code == "elwynn-earthroot");
        Assert.True(point.IsUnlocked);
        Assert.Equal(1, point.AvailableOpportunities);

        var (started, error) = await test.Service.StartAsync(test.Token,
            new StartGatheringRequest { CharacterId = test.First.Id, PointCode = point.Code });
        Assert.Null(error);
        Assert.True(started!.ActiveTask!.IsRare);
        Assert.Equal(TimeSpan.FromSeconds(20), started.ActiveTask.EndsAtUtc - started.ActiveTask.StartedAtUtc);
        var (stopped, stopError) = await test.Service.StopAsync(test.Token, started.ActiveTask.Id);
        Assert.Null(stopError);
        Assert.Equal("Stopped", stopped!.RecentTasks[0].Status);
        Assert.Equal(1, stopped.Points.Single(item => item.Code == point.Code).AvailableOpportunities);
        Assert.Empty(await test.Db.CharacterItemStacks.ToListAsync());

        var (restarted, retryError) = await test.Service.StartAsync(test.Token,
            new StartGatheringRequest { CharacterId = test.First.Id, PointCode = point.Code });
        Assert.Null(retryError);
        var task = restarted!.ActiveTask!;
        Assert.Null(await test.Service.AdvanceDueAsync(task.Id, task.EndsAtUtc));
        Assert.Null(await test.Service.AdvanceDueAsync(task.Id, task.EndsAtUtc.AddSeconds(20)));

        var completed = await test.Db.GatheringTasks.FindAsync(task.Id);
        Assert.Equal("Completed", completed!.Status);
        Assert.Equal((1, 1), (completed.CompletedCycles, completed.TotalQuantity));
        Assert.Equal(1, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
        var opportunity = await test.Db.CharacterGatheringOpportunities.SingleAsync();
        Assert.Equal((0, 1, 1), (opportunity.AvailableCount, opportunity.EarnedCount, opportunity.SpentCount));
        Assert.Empty(await test.Db.CharacterActivities.ToListAsync());
        var (_, noChanceError) = await test.Service.StartAsync(test.Token,
            new StartGatheringRequest { CharacterId = test.First.Id, PointCode = point.Code });
        Assert.Equal("NoGatheringOpportunity", noChanceError);
    }

    [Fact]
    public async Task EliteVictoryGrantsOnlyActualParticipantsOncePerRun()
    {
        await using var test = await GatheringTestContext.CreateAsync();
        var dungeon = new Dungeon
        {
            Id = 1, Code = "elwynn-grizzled-bear", Name = "林地灰熊王", DungeonKind = "Elite",
            MonsterName = "林地灰熊王", MonsterMaxHp = 100, MonsterAttack = 10, MonsterDefense = 2
        };
        test.Db.Dungeons.Add(dungeon);
        await test.Db.SaveChangesAsync();
        var rewards = RewardTestFactory.CreateService(test.Db, ProgressionTestFactory.Create());
        var run = new DungeonRunService(test.Db, rewards,
            battleMilestones: new BattleMilestoneService(test.Db),
            gatheringOpportunities: new GatheringOpportunityService(test.Db, test.Catalog));

        async Task DefeatAsync(int id)
        {
            var room = new Room
            {
                Id = id, DungeonId = dungeon.Id, MonsterId = id, OwnerUserId = test.Owner.Id,
                SlotCount = 5, Status = RoomStatus.Preparing
            };
            var monster = new Monster
            {
                Id = id, RoomId = id, WaveNumber = 1, Position = 1,
                Name = "林地灰熊王", MaxHp = 100, Hp = 0,
                RewardProfileCode = dungeon.Code
            };
            test.Db.AddRange(room, monster);
            await test.Db.SaveChangesAsync();
            var participants = new[]
            {
                new RewardParticipant(test.Owner.Id, test.First),
                new RewardParticipant(test.Owner.Id, test.Second)
            };
            await run.AdvanceAfterDefeatAsync(room, monster, participants, DateTime.UtcNow, [],
                [test.First.Id], [test.First.Id]);
            await test.Db.SaveChangesAsync();
            await run.AdvanceAfterDefeatAsync(room, monster, participants, DateTime.UtcNow, [],
                [test.First.Id], [test.First.Id]);
            await test.Db.SaveChangesAsync();
        }

        await DefeatAsync(1);
        var first = await test.Db.CharacterGatheringOpportunities.SingleAsync();
        Assert.Equal(test.First.Id, first.CharacterId);
        Assert.Equal(1, first.AvailableCount);
        var (firstView, _) = await test.Service.GetAsync(test.Token);
        Assert.Equal(1, firstView!.Points.Single(point => point.Code == "elwynn-earthroot").AvailableOpportunities);
        test.Owner.ActiveCharacterId = test.Second.Id;
        test.Owner.Version++;
        test.Db.CharacterBattleMilestones.Add(new CharacterBattleMilestone
        {
            CharacterId = test.Second.Id, Kind = BattleMilestoneService.MonsterKillKind,
            TargetCode = dungeon.Code, Count = 1,
            FirstAtUtc = DateTime.UtcNow, LastAtUtc = DateTime.UtcNow
        });
        await test.Db.SaveChangesAsync();
        var (secondView, _) = await test.Service.GetAsync(test.Token);
        Assert.Equal(0, secondView!.Points.Single(point => point.Code == "elwynn-earthroot").AvailableOpportunities);
        var (_, secondError) = await test.Service.StartAsync(test.Token,
            new StartGatheringRequest { CharacterId = test.Second.Id, PointCode = "elwynn-earthroot" });
        Assert.Equal("NoGatheringOpportunity", secondError);

        await DefeatAsync(2);
        Assert.Equal(2, (await test.Db.CharacterGatheringOpportunities.SingleAsync()).AvailableCount);
        Assert.Equal(2, (await test.Db.CharacterBattleMilestones.SingleAsync(item =>
            item.CharacterId == test.First.Id && item.TargetCode == dungeon.Code &&
            item.Kind == BattleMilestoneService.MonsterKillKind)).Count);
        Assert.Equal(0, await test.Db.CharacterGatheringOpportunities.CountAsync(item =>
            item.CharacterId == test.Second.Id));
    }

    private sealed class GatheringTestContext : IAsyncDisposable
    {
        private readonly string _path;
        private readonly WorldCatalog _world;
        private readonly MaterialCatalog _materials;
        private GatheringTestContext(string path, GameDbContext db, User owner, Character first, Character second,
            GatheringService service, GatheringCatalog catalog, WorldCatalog world, MaterialCatalog materials)
        {
            _path = path;
            Db = db;
            Owner = owner;
            First = first;
            Second = second;
            Service = service;
            Catalog = catalog;
            _world = world;
            _materials = materials;
        }

        public GameDbContext Db { get; }
        public User Owner { get; }
        public Character First { get; }
        public Character Second { get; }
        public GatheringService Service { get; }
        public GatheringCatalog Catalog { get; }
        public string Token => "gathering-owner-token";

        public GatheringService NewService(ProfessionCatalog professions) => new(Db,
            new UserService(Db, ProgressionTestFactory.Create(), SkillTestFactory.Create()),
            Catalog, _world, _materials, Options.Create(new ActivityOptions { MaximumHours = 12 }), professions);

        public static async Task<GatheringTestContext> CreateAsync(int cycleSeconds = 20)
        {
            var path = Path.Combine(Path.GetTempPath(), $"idlegame-gathering-{Guid.NewGuid():N}.db");
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options);
            await db.Database.EnsureCreatedAsync();
            var owner = new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 };
            var first = new Character { Id = 1, UserId = 1, Name = "First", Level = 10, Hp = 100, MaxHp = 100, Attack = 20 };
            var second = new Character { Id = 2, UserId = 1, Name = "Second", Level = 10, Hp = 100, MaxHp = 100, Attack = 20 };
            db.AddRange(owner, first, second,
                new CharacterBattleMilestone
                {
                    CharacterId = 1, Kind = BattleMilestoneService.MonsterKillKind,
                    TargetCode = "northshire-wolves", Count = 1,
                    FirstAtUtc = DateTime.UtcNow, LastAtUtc = DateTime.UtcNow
                },
                new UserLoginSession
                {
                    UserId = 1, Token = "gathering-owner-token",
                    CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1)
                });
            await db.SaveChangesAsync();
            var world = WorldCatalog.LoadDefault();
            var materials = new MaterialCatalog(Options.Create(new MaterialOptions
            {
                Items =
                [
                    new MaterialItemOptions { Code = "peacebloom", Name = "宁神花", Description = "测试" },
                    new MaterialItemOptions { Code = "silverleaf", Name = "银叶草", Description = "测试" },
                    new MaterialItemOptions { Code = "earthroot", Name = "地根草", Description = "测试" }
                ]
            }));
            var catalog = new GatheringCatalog(Options.Create(new GatheringOptions
            {
                Points =
                [
                    new GatheringPointOptions
                    {
                        Code = "elwynn-peacebloom", Name = "北郡宁神花", RegionCode = "elwynn",
                        MaterialCode = "peacebloom", CycleSeconds = cycleSeconds, OutputQuantity = 1,
                        UnlockKind = BattleMilestoneService.MonsterKillKind,
                        UnlockTargetCode = "northshire-wolves",
                        AlternativeUnlockTargetCodes = ["tirisfal-dusk-bat"]
                    },
                    new GatheringPointOptions
                    {
                        Code = "elwynn-silverleaf", Name = "矿道银叶草", RegionCode = "elwynn",
                        MaterialCode = "silverleaf", CycleSeconds = 20, OutputQuantity = 1,
                        MinimumCharacterLevel = 8, UnlockKind = BattleMilestoneService.DungeonClearKind,
                        UnlockTargetCode = "kobold-mine"
                    },
                    new GatheringPointOptions
                    {
                        Code = "elwynn-earthroot", Name = "灰熊巢地根草", RegionCode = "elwynn",
                        MaterialCode = "earthroot", BonusMaterialCode = "peacebloom", IsRare = true, CycleSeconds = 20, OutputQuantity = 1,
                        MinimumCharacterLevel = 9, UnlockKind = BattleMilestoneService.MonsterKillKind,
                        UnlockTargetCode = "elwynn-grizzled-bear"
                    }
                ]
            }), world, materials);
            var service = new GatheringService(db,
                new UserService(db, ProgressionTestFactory.Create(), SkillTestFactory.Create()),
                catalog, world, materials, Options.Create(new ActivityOptions { MaximumHours = 12 }));
            return new GatheringTestContext(path, db, owner, first, second, service, catalog, world, materials);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            File.Delete(_path);
        }
    }
}
