using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Gathering;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class GatheringServiceTests
{
    [Fact]
    public async Task UnlocksBelongToEachCharacterAndGatheringGoesToSharedWarehouse()
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
            TargetCode = "northshire-wolves", Count = 1,
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
        Assert.Equal(3, (await test.Db.UserWarehouseStacks.SingleAsync()).Quantity);
        var (secondView, _) = await test.Service.GetAsync(test.Token);
        Assert.Equal(3, secondView!.Points.Single(point => point.Code == "elwynn-peacebloom").WarehouseQuantity);
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
        Assert.Equal(2160, (await test.Db.UserWarehouseStacks.SingleAsync()).Quantity);
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
        Assert.Equal(2, (await test.Db.UserWarehouseStacks.SingleAsync()).Quantity);
    }

    private sealed class GatheringTestContext : IAsyncDisposable
    {
        private readonly string _path;
        private GatheringTestContext(string path, GameDbContext db, User owner, Character first, Character second,
            GatheringService service)
        {
            _path = path;
            Db = db;
            Owner = owner;
            First = first;
            Second = second;
            Service = service;
        }

        public GameDbContext Db { get; }
        public User Owner { get; }
        public Character First { get; }
        public Character Second { get; }
        public GatheringService Service { get; }
        public string Token => "gathering-owner-token";

        public static async Task<GatheringTestContext> CreateAsync(int cycleSeconds = 20)
        {
            var path = Path.Combine(Path.GetTempPath(), $"idlegame-gathering-{Guid.NewGuid():N}.db");
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options);
            await db.Database.EnsureCreatedAsync();
            var owner = new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 };
            var first = new Character { Id = 1, UserId = 1, Name = "First", Level = 8, Hp = 100, MaxHp = 100, Attack = 20 };
            var second = new Character { Id = 2, UserId = 1, Name = "Second", Level = 8, Hp = 100, MaxHp = 100, Attack = 20 };
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
                    new MaterialItemOptions { Code = "peacebloom", Name = "宁神花", Description = "测试", CanStoreInWarehouse = true },
                    new MaterialItemOptions { Code = "silverleaf", Name = "银叶草", Description = "测试", CanStoreInWarehouse = true }
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
                        UnlockTargetCode = "northshire-wolves"
                    },
                    new GatheringPointOptions
                    {
                        Code = "elwynn-silverleaf", Name = "矿道银叶草", RegionCode = "elwynn",
                        MaterialCode = "silverleaf", CycleSeconds = 20, OutputQuantity = 1,
                        MinimumCharacterLevel = 8, UnlockKind = BattleMilestoneService.DungeonClearKind,
                        UnlockTargetCode = "kobold-mine"
                    }
                ]
            }), world, materials);
            var service = new GatheringService(db,
                new UserService(db, ProgressionTestFactory.Create(), SkillTestFactory.Create()),
                catalog, world, materials, Options.Create(new ActivityOptions { MaximumHours = 12 }));
            return new GatheringTestContext(path, db, owner, first, second, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            File.Delete(_path);
        }
    }
}
