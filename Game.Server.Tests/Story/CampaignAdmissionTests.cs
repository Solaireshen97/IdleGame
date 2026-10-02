using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Dtos.Auth;
using Game.Shared.Dtos.Characters;
using Game.Shared.Dtos.Story;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests.Story;

public sealed class CampaignAdmissionTests
{
    [Fact]
    public async Task NewAccountCannotCreateAnyChallengeBeforeFirstDialogue()
    {
        await using var f = await Fixture.CreateAsync();
        var listed = await f.Rooms.GetDungeonsAsync("new-token");
        Assert.NotEmpty(listed);
        Assert.All(listed, d => Assert.False(d.CanEnter));
        foreach (var dungeon in await f.Db.Dungeons.Where(d => d.IsVisible).ToListAsync())
            Assert.Equal("StoryMapLocked", (await f.Rooms.CreateRoomAsync(dungeon.Id, null, "new-token")).Error);
        Assert.Empty(await f.Db.Rooms.ToListAsync());
    }

    [Fact]
    public async Task FirstDialogueUnlocksOnlyWolvesAndListMatchesAdmission()
    {
        await using var f = await Fixture.CreateAsync();
        await StoryService.StartTrackedAsync(f.Db, 1, 1, StoryQuestCatalog.Default, DateTime.UtcNow);
        await f.Db.SaveChangesAsync();
        var quest = await f.Db.StoryQuestProgress.SingleAsync();
        Assert.Equal("ReadyToTurnIn", quest.Status);
        var result = await new StoryService(f.Db, f.Users, StoryQuestCatalog.Default, depths: f.Depths)
            .TurnInAsync("new-token", "ch01-01", new StoryTurnInRequest { RequestId = Guid.NewGuid().ToString("N"), ExpectedVersion = quest.Version });
        Assert.Null(result.Error);
        Assert.Equal("northshire-wolves", (await f.Db.StoryMapUnlocks.SingleAsync()).MapNodeCode);
        var list = await f.Rooms.GetDungeonsAsync("new-token");
        Assert.Equal("northshire-wolves", Assert.Single(list, d => d.CanEnter).Code);
        foreach (var node in list)
        {
            var actual = await f.Rooms.CreateRoomAsync(node.DungeonId, null, "new-token");
            Assert.Equal(node.CanEnter, actual.Error is null);
            if (!node.CanEnter) Assert.Equal("StoryMapLocked", actual.Error);
            else Assert.Null((await f.Rooms.RemoveSlotAsync(actual.Detail!.RoomId, 1, "new-token")).Error);
        }
    }

    [Fact]
    public async Task PublicRoomAndQueuedJoinCannotBypassAnotherAccountsLock()
    {
        await using var f = await Fixture.CreateAsync();
        var boars = await f.Dungeon("stone-tusk-boars");
        var created = await f.Rooms.CreateRoomAsync(boars.Id, null, "old-token", isPublic: true);
        Assert.Null(created.Error);
        Assert.Equal("StoryMapLocked", (await f.Rooms.JoinRoomAsync(created.Detail!.RoomId,
            new JoinRoomRequest { CharacterId = 1, SlotIndex = 2 }, "new-token")).Error);
        Assert.Equal("StoryMapLocked", (await f.Rooms.SubmitOperationAsync(created.Detail.RoomId,
            new SubmitRoomOperationRequest { Kind = RoomOperationKind.Join, CharacterId = 1, SlotIndex = 2 }, "new-token")).Error);
        Assert.Empty(await f.Db.RoomOperations.ToListAsync());
        Assert.False(await f.Db.RoomSlots.AnyAsync(s => s.CharacterId == 1));
    }

    [Fact]
    public async Task AssignAndQueueSubmissionRecheckOwnersMapAccess()
    {
        await using var f = await Fixture.CreateAsync();
        var state = (await f.Db.UserStoryStates.FindAsync(1))!;
        state.IsLegacy = true;
        await f.Db.SaveChangesAsync();
        var boars = await f.Dungeon("stone-tusk-boars");
        var created = await f.Rooms.CreateRoomAsync(boars.Id, null, "new-token");
        Assert.Null(created.Error);
        state.IsLegacy = false;
        await f.Db.SaveChangesAsync();
        Assert.Equal("StoryMapLocked", (await f.Rooms.AssignSlotAsync(created.Detail!.RoomId,
            new AssignRoomSlotRequest { CharacterId = 3, SlotIndex = 2 }, "new-token")).Error);
        Assert.Equal("StoryMapLocked", (await f.Rooms.SubmitOperationAsync(created.Detail.RoomId,
            new SubmitRoomOperationRequest { Kind = RoomOperationKind.Assign, CharacterId = 3, SlotIndex = 2 }, "new-token")).Error);
        Assert.False(await f.Db.RoomSlots.AnyAsync(s => s.CharacterId == 3));
    }

    [Fact]
    public async Task QueuedAdmissionRechecksAuthorizationWhenItExecutes()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Unlock("stone-tusk-boars");
        var boars = await f.Dungeon("stone-tusk-boars");
        var created = await f.Rooms.CreateRoomAsync(boars.Id, null, "new-token", isPublic: true);
        Assert.Null(created.Error);
        var room = (await f.Db.Rooms.FindAsync(created.Detail!.RoomId))!;
        room.Status = RoomStatus.Cooldown; room.RoundNumber = 1;
        await f.Db.SaveChangesAsync();
        Assert.Null((await f.Rooms.SubmitOperationAsync(room.Id,
            new SubmitRoomOperationRequest { Kind = RoomOperationKind.Assign, CharacterId = 3, SlotIndex = 2 }, "new-token")).Error);
        Assert.Equal("Pending", (await f.Db.RoomOperations.SingleAsync()).Status);
        f.Db.StoryMapUnlocks.Remove(await f.Db.StoryMapUnlocks.SingleAsync());
        room.Status = RoomStatus.BattleOver;
        await f.Db.SaveChangesAsync();
        await f.Rooms.ProcessPendingOperationsAsync(room.Id);
        var operation = await f.Db.RoomOperations.SingleAsync();
        Assert.Equal("Failed", operation.Status);
        Assert.Equal("StoryMapLocked", operation.Error);
        Assert.False(await f.Db.RoomSlots.AnyAsync(s => s.CharacterId == 3));
        Assert.False(await f.Db.CharacterActivities.AnyAsync(a => a.CharacterId == 3));
    }

    [Fact]
    public async Task OtherOwnedCharacterSharesAccountMapAuthorization()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Unlock("northshire-wolves");
        var wolves = await f.Dungeon("northshire-wolves");
        var created = await f.Rooms.CreateRoomAsync(wolves.Id, null, "new-token", characterId: 3);
        Assert.Null(created.Error);
        Assert.Equal(3, Assert.Single(created.Detail!.Slots, s => s.IsOccupied).CharacterId);
        var milestone = await f.Db.CharacterBattleMilestones.Where(x => x.CharacterId == 3).ToListAsync();
        Assert.Empty(milestone);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportedAndLegacyAccountsRetainOriginalCampaignAccess(bool explicitLegacy)
    {
        await using var f = await Fixture.CreateAsync();
        if (explicitLegacy) f.Db.UserStoryStates.Add(new UserStoryState { UserId = 2, IsLegacy = true });
        await f.Db.SaveChangesAsync();
        var campaign = new CampaignAccessService(f.Db, f.Depths);
        foreach (var dungeon in await f.Db.Dungeons.Where(d => d.IsVisible).ToListAsync())
            Assert.Null(await campaign.AdmissionErrorAsync(2, dungeon));
        var boars = await f.Dungeon("stone-tusk-boars");
        Assert.Null((await f.Rooms.CreateRoomAsync(boars.Id, null, "old-token")).Error);
    }

    [Fact]
    public async Task RealOrdinaryClearOpensDepthBeforeQuestTurnInAndKeepsDepthRules()
    {
        await using var f = await Fixture.CreateAsync();
        var mine = await f.Dungeon("kobold-mine");
        var deep = await f.Dungeon("kobold-mine-depths");
        Assert.Equal("StoryMapLocked", await new CampaignAccessService(f.Db, f.Depths).AdmissionErrorAsync(1, deep));
        f.Db.UserDungeonClears.Add(new UserDungeonClear { UserId = 1, DungeonId = mine.Id, HighestDepth = 1, ClearedAtUtc = DateTime.UtcNow });
        await f.Db.SaveChangesAsync();
        Assert.Empty(await f.Db.StoryActionReceipts.ToListAsync());
        Assert.Empty(await f.Db.StoryMapUnlocks.ToListAsync());
        Assert.True((await f.Rooms.GetDungeonsAsync("new-token")).Single(d => d.Code == deep.Code).CanEnter);
        Assert.Null((await f.Rooms.CreateRoomAsync(deep.Id, null, "new-token", depthLevel: 1)).Error);
        Assert.Equal("DungeonDepthLocked", (await f.Rooms.CreateRoomAsync(deep.Id, null, "new-token", depthLevel: 2, characterId: 3)).Error);
    }

    [Fact]
    public async Task RegistrationAndFirstCharacterActivateFirstQuestWithoutStoryRead()
    {
        await using var f = await Fixture.CreateAsync();
        var registration = await f.Users.RegisterAsync(new RegisterRequest { UserName = "registered", Password = "password" });
        Assert.Null(registration.Error);
        var id = registration.Response!.UserId;
        Assert.False((await f.Db.UserStoryStates.FindAsync(id))!.IsLegacy);
        var created = await f.Users.CreateCurrentCharacterAsync(registration.Response.Token, new CreateCharacterRequest { Name = "New traveler", ProfessionCode = "swordsman" });
        Assert.Null(created.Error);
        var state = (await f.Db.UserStoryStates.FindAsync(id))!;
        Assert.Equal("ch01-01", state.CurrentQuestCode);
        Assert.Equal(created.Response!.CharacterId, state.TutorialCharacterId);
        var quest = await f.Db.StoryQuestProgress.SingleAsync(q => q.UserId == id);
        Assert.Equal("ch01-01", quest.QuestCode);
        Assert.Equal("ReadyToTurnIn", quest.Status);
        Assert.Equal(created.Response.CharacterId, quest.ActorCharacterId);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string path;
        public GameDbContext Db { get; }
        public UserService Users { get; }
        public RoomService Rooms { get; }
        public DungeonDepthCatalog Depths { get; }
        private Fixture(string path, GameDbContext db)
        {
            this.path = path; Db = db;
            var configuration = new ConfigurationBuilder().AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
            Depths = new DungeonDepthCatalog(Options.Create(configuration.GetSection(DungeonDepthOptions.SectionName).Get<DungeonDepthOptions>()!));
            var progression = ProgressionTestFactory.Create(); var skills = SkillTestFactory.Create();
            var weapons = new WeaponCatalog(Options.Create(configuration.GetSection(WeaponOptions.SectionName).Get<WeaponOptions>()!));
            Users = new UserService(db, progression, skills, weapons, storyCatalog: StoryQuestCatalog.Default);
            Rooms = new RoomService(db, Users, progression, ConsumableTestFactory.Create(), skills,
                RewardTestFactory.CreateService(db, progression), depthCatalog: Depths);
        }
        public static async Task<Fixture> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"idle-campaign-admission-{Guid.NewGuid():N}.db");
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
            await db.Database.EnsureCreatedAsync();
            db.Dungeons.AddRange(WorldCatalog.LoadDefault().Dungeons);
            db.AddRange(new User { Id = 1, UserName = "new", PasswordHash = "hash", ActiveCharacterId = 1 },
                new User { Id = 2, UserName = "old", PasswordHash = "hash", ActiveCharacterId = 2 },
                new Character { Id = 1, UserId = 1, Name = "First", Hp = 100, MaxHp = 100, Attack = 20 },
                new Character { Id = 2, UserId = 2, Name = "Old", Hp = 100, MaxHp = 100, Attack = 20 },
                new Character { Id = 3, UserId = 1, Name = "Second", Hp = 100, MaxHp = 100, Attack = 20 },
                new UserStoryState { UserId = 1, IsLegacy = false },
                new UserLoginSession { UserId = 1, Token = "new-token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) },
                new UserLoginSession { UserId = 2, Token = "old-token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();
            return new Fixture(path, db);
        }
        public Task<Dungeon> Dungeon(string code) => Db.Dungeons.SingleAsync(d => d.Code == code);
        public async Task Unlock(string code)
        {
            Db.StoryMapUnlocks.Add(new StoryMapUnlock { UserId = 1, MapNodeCode = code, SourceQuestCode = "test", UnlockedAtUtc = DateTime.UtcNow });
            await Db.SaveChangesAsync();
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); File.Delete(path); }
    }
}
