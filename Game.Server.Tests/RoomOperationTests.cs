using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Game.Server.Tests;

public partial class RoomServiceTests
{
    [Theory]
    [InlineData(RoomStatus.Preparing)]
    [InlineData(RoomStatus.Cooldown)]
    [InlineData(RoomStatus.NotStarted)]
    [InlineData(RoomStatus.WaveTransition)]
    public async Task QueuedAssignmentPreservesFormationUntilWholeBattleEnds(RoomStatus status)
    {
        await using var test = await RoomTestContext.CreateAsync();
        var room = await CreateRunningRoom(test, status);
        var character = await test.AddCharacterAsync("Priest");
        var request = AssignOperation(5, character.Id);

        var (queued, error) = await test.Service.SubmitOperationAsync(room.Id, request, test.Token);
        Assert.Null(error);
        Assert.Equal("Pending", Assert.Single(queued!.Operations).Status);
        Assert.False(queued.Slots.Single(slot => slot.SlotIndex == 5).IsOccupied);
        Assert.False(await test.Db.CharacterActivities.AnyAsync(item => item.CharacterId == character.Id));
        Assert.Equal("FormationLocked", (await test.Service.AssignSlotAsync(room.Id,
            new AssignRoomSlotRequest { SlotIndex = 5, CharacterId = character.Id }, test.Token)).Error);
        await test.Service.ProcessPendingOperationsAsync(room.Id);
        Assert.Equal("Pending", (await test.Db.RoomOperations.SingleAsync()).Status);

        room.Status = RoomStatus.BattleOver;
        await test.Db.SaveChangesAsync();
        await test.Service.ProcessPendingOperationsAsync(room.Id);
        Assert.Equal(character.Id, (await test.Db.RoomSlots.SingleAsync(slot => slot.RoomId == room.Id && slot.SlotIndex == 5)).CharacterId);
        Assert.Equal("Completed", (await test.Db.RoomOperations.SingleAsync()).Status);
        Assert.Equal(room.Id, (await test.Db.CharacterActivities.SingleAsync(item => item.CharacterId == character.Id)).SourceId);
        var version = room.Version;
        await test.Service.ProcessPendingOperationsAsync(room.Id);
        Assert.Equal(version, room.Version);
    }

    [Fact]
    public async Task QueuedSwapIsAtomicAndPreservesEachCharactersHpAndState()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var other = await test.AddCharacterAsync("Priest");
        await test.Service.AssignSlotAsync(created!.RoomId, new AssignRoomSlotRequest { SlotIndex = 5, CharacterId = other.Id }, test.Token);
        var room = (await test.Db.Rooms.FindAsync(created.RoomId))!;
        room.Status = RoomStatus.Cooldown; room.RoundNumber = 1; room.IsOwnerAutoEnabled = true;
        test.ActiveCharacter.Hp = 19; other.Hp = 36;
        var source = await test.Db.RoomSlots.SingleAsync(slot => slot.RoomId == room.Id && slot.SlotIndex == 1);
        source.HasParticipatedInRun = true; source.PendingSkillSlotMask = 2;
        await test.Db.SaveChangesAsync();
        Assert.Null((await test.Service.SubmitOperationAsync(room.Id, AssignOperation(5, test.ActiveCharacter.Id), test.Token)).Error);
        Assert.Equal(1, (await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == test.ActiveCharacter.Id)).SlotIndex);

        room.Status = RoomStatus.BattleOver;
        await test.Db.SaveChangesAsync();
        await test.Service.ProcessPendingOperationsAsync(room.Id);
        Assert.Equal(5, (await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == test.ActiveCharacter.Id)).SlotIndex);
        Assert.Equal(1, (await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == other.Id)).SlotIndex);
        Assert.Equal((19, 36), (test.ActiveCharacter.Hp, other.Hp));
        Assert.True((await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == test.ActiveCharacter.Id)).HasParticipatedInRun);
        Assert.Equal(2, (await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == test.ActiveCharacter.Id)).PendingSkillSlotMask);
        Assert.True(room.IsOwnerAutoEnabled);
    }

    [Fact]
    public async Task IndependentRequestsCoexistAndDuplicatesDoNotCreateMoreRequests()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var room = await CreateRunningRoom(test);
        var first = await test.AddCharacterAsync("B");
        var second = await test.AddCharacterAsync("C");
        Assert.Null((await test.Service.SubmitOperationAsync(room.Id, AssignOperation(2, first.Id), test.Token)).Error);
        Assert.Null((await test.Service.SubmitOperationAsync(room.Id, AssignOperation(2, first.Id), test.Token)).Error);
        Assert.Null((await test.Service.SubmitOperationAsync(room.Id, AssignOperation(3, second.Id), test.Token)).Error);
        Assert.Equal(2, await test.Db.RoomOperations.CountAsync());
        room.Status = RoomStatus.BattleOver;
        await test.Db.SaveChangesAsync();
        await test.Service.ProcessPendingOperationsAsync(room.Id);
        Assert.Equal(2, await test.Db.RoomOperations.CountAsync(item => item.Status == "Completed"));
        Assert.Equal(3, await test.Db.RoomSlots.CountAsync(slot => slot.RoomId == room.Id && slot.CharacterId.HasValue));
    }

    [Fact]
    public async Task NewOverlappingChoiceReplacesOldOneAndOnlyLatestChoiceRuns()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var room = await CreateRunningRoom(test);
        Assert.Null((await test.Service.SubmitOperationAsync(room.Id, AssignOperation(5, test.ActiveCharacter.Id), test.Token)).Error);
        Assert.Null((await test.Service.SubmitOperationAsync(room.Id, AssignOperation(4, test.ActiveCharacter.Id), test.Token)).Error);
        Assert.Equal("Cancelled", (await test.Db.RoomOperations.OrderBy(item => item.Id).FirstAsync()).Status);
        room.Status = RoomStatus.BattleOver;
        await test.Db.SaveChangesAsync();
        await test.Service.ProcessPendingOperationsAsync(room.Id);
        Assert.Equal(4, (await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == test.ActiveCharacter.Id)).SlotIndex);
    }

    [Fact]
    public async Task CancellationPersistsAndCannotBePerformedByAnotherUser()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var room = await CreateRunningRoom(test);
        await test.AddOtherActiveCharacterAsync();
        await test.Service.SubmitOperationAsync(room.Id, AssignOperation(5, test.ActiveCharacter.Id), test.Token);
        var operation = await test.Db.RoomOperations.SingleAsync();
        Assert.Equal("NotFound", (await test.Service.CancelOperationAsync(room.Id, operation.Id, "other-token")).Error);
        Assert.True((await test.Service.CancelOperationAsync(room.Id, operation.Id, test.Token)).Success);
        room.Status = RoomStatus.BattleOver;
        await test.Db.SaveChangesAsync();
        await using var db = test.CreateDbContext();
        await MakeRoomService(db).ProcessPendingOperationsAsync(room.Id);
        Assert.Equal("Cancelled", (await db.RoomOperations.SingleAsync()).Status);
        Assert.Equal(1, (await db.RoomSlots.SingleAsync(slot => slot.CharacterId == test.ActiveCharacter.Id)).SlotIndex);
        Assert.Equal("OperationAlreadyFinished", (await test.Service.CancelOperationAsync(room.Id, operation.Id, test.Token)).Error);
    }

    [Fact]
    public async Task ChangedTargetFailsWithoutRemovingOrOverwritingItsNewOccupant()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var room = await CreateRunningRoom(test);
        var other = await test.AddCharacterAsync("B");
        await test.Service.SubmitOperationAsync(room.Id, AssignOperation(5, test.ActiveCharacter.Id), test.Token);
        room.Status = RoomStatus.BattleOver;
        await test.Db.SaveChangesAsync();
        await test.Service.AssignSlotAsync(room.Id, new AssignRoomSlotRequest { SlotIndex = 5, CharacterId = other.Id }, test.Token);
        await test.Service.ProcessPendingOperationsAsync(room.Id);
        Assert.Equal("FormationChanged", (await test.Db.RoomOperations.SingleAsync()).Error);
        Assert.Equal("Failed", (await test.Db.RoomOperations.SingleAsync()).Status);
        Assert.Equal(other.Id, (await test.Db.RoomSlots.SingleAsync(slot => slot.RoomId == room.Id && slot.SlotIndex == 5)).CharacterId);
        Assert.Equal(1, (await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == test.ActiveCharacter.Id)).SlotIndex);
    }

    [Fact]
    public async Task BusyCharacterAtExecutionFailsWithoutStartingASecondActivity()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var room = await CreateRunningRoom(test);
        var other = await test.AddCharacterAsync("B");
        await test.Service.SubmitOperationAsync(room.Id, AssignOperation(5, other.Id), test.Token);
        test.Db.CharacterActivities.Add(new CharacterActivity { CharacterId = other.Id, Kind = "Gathering", SourceId = 73, StartedAtUtc = DateTime.UtcNow });
        room.Status = RoomStatus.BattleOver;
        await test.Db.SaveChangesAsync();
        await test.Service.ProcessPendingOperationsAsync(room.Id);
        Assert.Equal("CharacterAlreadyInRoom", (await test.Db.RoomOperations.SingleAsync()).Error);
        Assert.Equal("Gathering", (await test.Db.CharacterActivities.SingleAsync(item => item.CharacterId == other.Id)).Kind);
        Assert.False(await test.Db.RoomSlots.AnyAsync(slot => slot.CharacterId == other.Id));
    }

    [Theory]
    [InlineData(RoomStatus.NotStarted)]
    [InlineData(RoomStatus.Preparing)]
    [InlineData(RoomStatus.Cooldown)]
    [InlineData(RoomStatus.WaveTransition)]
    public async Task JoinExecutesImmediatelyInEveryCurrentlyAllowedPhase(RoomStatus status)
    {
        await using var test = await RoomTestContext.CreateAsync();
        var room = await CreateRunningRoom(test, status);
        await test.AddOtherActiveCharacterAsync();
        var (detail, error) = await test.Service.SubmitOperationAsync(room.Id,
            new SubmitRoomOperationRequest { Kind = RoomOperationKind.Join, SlotIndex = 3 }, "other-token");
        Assert.Null(error);
        Assert.Equal("Completed", Assert.Single(detail!.Operations).Status);
        Assert.Equal(2, detail.Slots.Single(slot => slot.SlotIndex == 3).CharacterId);
        Assert.Empty((await test.Service.GetRoomDetailAsync(room.Id, test.Token))!.Operations);
    }

    [Fact]
    public async Task QueuedGuestLeaveSurvivesSwitchingAccountCharacterAndClosingAdmission()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isPublic: true);
        await test.AddOtherActiveCharacterAsync();
        await test.Service.JoinRoomAsync(created!.RoomId, new JoinRoomRequest { SlotIndex = 2 }, "other-token");
        var room = (await test.Db.Rooms.FindAsync(created.RoomId))!;
        room.Status = RoomStatus.Cooldown; room.RoundNumber = 1;
        await test.Db.SaveChangesAsync();
        var (queued, error) = await test.Service.SubmitOperationAsync(room.Id,
            new SubmitRoomOperationRequest { Kind = RoomOperationKind.Leave }, "other-token");
        Assert.Null(error);
        Assert.Equal("Pending", Assert.Single(queued!.Operations).Status);
        (await test.Db.Users.FindAsync(2))!.ActiveCharacterId = null;
        room.IsPublic = false; room.Status = RoomStatus.BattleOver;
        await test.Db.SaveChangesAsync();
        await test.Service.ProcessPendingOperationsAsync(room.Id);
        Assert.False(await test.Db.RoomSlots.AnyAsync(slot => slot.RoomId == room.Id && slot.UserId == 2));
        Assert.False(await test.Db.CharacterActivities.AnyAsync(activity => activity.CharacterId == 2));
        Assert.Null(await test.Service.GetRoomDetailAsync(room.Id, "other-token"));
        Assert.Equal("Completed", Assert.Single((await test.Service.GetOperationsAsync(room.Id, "other-token")).Operations!).Status);
        Assert.Empty((await test.Service.GetOperationsAsync(room.Id, test.Token)).Operations!);
    }

    [Fact]
    public async Task ClosedRoomFailsPendingRequestsWithoutRestoringOccupants()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var room = await CreateRunningRoom(test);
        await test.Service.SubmitOperationAsync(room.Id, AssignOperation(5, test.ActiveCharacter.Id), test.Token);
        await CharacterActivityManager.CloseBattleRoomAsync(test.Db, room, DateTime.UtcNow);
        await test.Db.SaveChangesAsync();
        await test.Service.ProcessPendingOperationsAsync(room.Id);
        Assert.Equal("RoomClosed", (await test.Db.RoomOperations.SingleAsync()).Error);
        Assert.False(await test.Db.RoomSlots.AnyAsync(slot => slot.RoomId == room.Id && slot.CharacterId.HasValue));
    }

    [Fact]
    public async Task RequestsExecuteBeforeRepeatBattleRestartsEvenWithoutAnOpenPage()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isRepeatBattle: true);
        var room = (await test.Db.Rooms.FindAsync(created!.RoomId))!;
        room.Status = RoomStatus.Cooldown; room.RoundNumber = 1;
        await test.Db.SaveChangesAsync();
        await test.Service.SubmitOperationAsync(room.Id, AssignOperation(5, test.ActiveCharacter.Id), test.Token);
        room.Status = RoomStatus.BattleOver;
        room.BattleEndedAtUtc = DateTime.UtcNow.AddSeconds(-10);
        (await test.Db.Monsters.FindAsync(room.MonsterId))!.Hp = 0;
        await test.Db.SaveChangesAsync();
        await using var db = test.CreateDbContext();
        var progression = ProgressionTestFactory.Create(); var skills = SkillTestFactory.Create();
        var battle = new BattleService(db, new UserService(db, progression, skills), ConsumableTestFactory.Create(), skills,
            RewardTestFactory.CreateService(db, progression), roomService: MakeRoomService(db));
        Assert.Null((await battle.SyncRoomAsync(room.Id)).Error);
        Assert.Equal("Completed", (await db.RoomOperations.SingleAsync()).Status);
        Assert.Equal(5, (await db.RoomSlots.SingleAsync(slot => slot.CharacterId == test.ActiveCharacter.Id)).SlotIndex);
        Assert.Equal(2, (await db.Rooms.FindAsync(room.Id))!.RunSequence);
    }

    [Fact]
    public async Task HostedWorkerExecutesQueuedRemovalInSingleBattleAfterSettlement()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var room = await CreateRunningRoom(test);
        await test.Service.SubmitOperationAsync(room.Id, new SubmitRoomOperationRequest { Kind = RoomOperationKind.Remove, SlotIndex = 1 }, test.Token);
        room.Status = RoomStatus.BattleOver;
        await test.Db.SaveChangesAsync();
        var services = new ServiceCollection();
        services.AddDbContext<GameDbContext>(options => options.UseSqlite(test.Db.Database.GetConnectionString()!));
        services.AddScoped(provider => MakeRoomService(provider.GetRequiredService<GameDbContext>()));
        services.AddScoped(provider =>
        {
            var db = provider.GetRequiredService<GameDbContext>(); var progression = ProgressionTestFactory.Create(); var skills = SkillTestFactory.Create();
            return new BattleService(db, new UserService(db, progression, skills), ConsumableTestFactory.Create(), skills,
                RewardTestFactory.CreateService(db, progression), roomService: provider.GetRequiredService<RoomService>());
        });
        await using var provider = services.BuildServiceProvider();
        using var worker = new RoomCycleService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<RoomCycleService>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5); string? status = null;
            while (DateTime.UtcNow < deadline)
            {
                await using var check = test.CreateDbContext();
                status = (await check.RoomOperations.AsNoTracking().SingleAsync()).Status;
                if (status == "Completed") break;
                await Task.Delay(50);
            }
            Assert.Equal("Completed", status);
            await using var final = test.CreateDbContext();
            Assert.Empty(await final.CharacterActivities.ToListAsync());
            Assert.False(await final.RoomSlots.AnyAsync(slot => slot.RoomId == room.Id && slot.CharacterId.HasValue));
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task EnqueueStillEnforcesAuthenticationOwnershipAndAdmission()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var room = await CreateRunningRoom(test);
        await test.AddOtherActiveCharacterAsync();
        Assert.Equal("Unauthorized", (await test.Service.SubmitOperationAsync(room.Id, AssignOperation(5, 1), null)).Error);
        Assert.Equal("NotCharacterOwner", (await test.Service.SubmitOperationAsync(room.Id, AssignOperation(5, 1), "other-token")).Error);
        Assert.Equal("NotCharacterOwner", (await test.Service.SubmitOperationAsync(room.Id, AssignOperation(5, 2), test.Token)).Error);
        room.IsPublic = false;
        await test.Db.SaveChangesAsync();
        Assert.Equal("RoomPrivate", (await test.Service.SubmitOperationAsync(room.Id,
            new SubmitRoomOperationRequest { Kind = RoomOperationKind.Join, SlotIndex = 2 }, "other-token")).Error);
        Assert.Empty(await test.Db.RoomOperations.ToListAsync());
    }

    private static SubmitRoomOperationRequest AssignOperation(int slot, int character) =>
        new() { Kind = RoomOperationKind.Assign, SlotIndex = slot, CharacterId = character };

    [Fact]
    public async Task ConcurrentWorkersApplyAQueuedSwapOnlyOnce()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var other = await test.AddCharacterAsync("Priest");
        await test.Service.AssignSlotAsync(created!.RoomId, new AssignRoomSlotRequest { SlotIndex = 5, CharacterId = other.Id }, test.Token);
        var room = (await test.Db.Rooms.FindAsync(created.RoomId))!;
        room.Status = RoomStatus.Cooldown; room.RoundNumber = 1;
        await test.Db.SaveChangesAsync();
        await test.Service.SubmitOperationAsync(room.Id, AssignOperation(5, test.ActiveCharacter.Id), test.Token);
        room.Status = RoomStatus.BattleOver;
        await test.Db.SaveChangesAsync();
        await using var first = test.CreateDbContext();
        await using var second = test.CreateDbContext();
        await Task.WhenAll(MakeRoomService(first).ProcessPendingOperationsAsync(room.Id), MakeRoomService(second).ProcessPendingOperationsAsync(room.Id));
        await using var final = test.CreateDbContext();
        Assert.Equal("Completed", (await final.RoomOperations.SingleAsync()).Status);
        Assert.Equal(5, (await final.RoomSlots.SingleAsync(slot => slot.CharacterId == test.ActiveCharacter.Id)).SlotIndex);
        Assert.Equal(1, (await final.RoomSlots.SingleAsync(slot => slot.CharacterId == other.Id)).SlotIndex);
        Assert.Equal(2, await final.CharacterActivities.CountAsync());
    }

    [Fact]
    public async Task CancellationRacingExecutionHasOnlyOneConsistentOutcome()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var room = await CreateRunningRoom(test);
        await test.Service.SubmitOperationAsync(room.Id, AssignOperation(5, test.ActiveCharacter.Id), test.Token);
        var operationId = (await test.Db.RoomOperations.SingleAsync()).Id;
        room.Status = RoomStatus.BattleOver;
        await test.Db.SaveChangesAsync();
        await using var first = test.CreateDbContext();
        await using var second = test.CreateDbContext();
        await Task.WhenAll(MakeRoomService(first).ProcessPendingOperationsAsync(room.Id),
            MakeRoomService(second).CancelOperationAsync(room.Id, operationId, test.Token));
        await using var final = test.CreateDbContext();
        var operation = await final.RoomOperations.SingleAsync();
        Assert.Contains(operation.Status, new[] { "Cancelled", "Completed" });
        Assert.Equal(operation.Status == "Completed" ? 5 : 1,
            (await final.RoomSlots.SingleAsync(slot => slot.CharacterId == test.ActiveCharacter.Id)).SlotIndex);
        Assert.Single(await final.CharacterActivities.ToListAsync());
    }

    private static RoomService MakeRoomService(GameDbContext db)
    {
        var progression = ProgressionTestFactory.Create(); var skills = SkillTestFactory.Create();
        return new RoomService(db, new UserService(db, progression, skills), progression, ConsumableTestFactory.Create(), skills,
            RewardTestFactory.CreateService(db, progression));
    }

    private static async Task<Room> CreateRunningRoom(RoomTestContext test, RoomStatus status = RoomStatus.Cooldown)
    {
        var (created, error) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isPublic: true);
        Assert.Null(error);
        var room = (await test.Db.Rooms.FindAsync(created!.RoomId))!;
        room.Status = status; room.RoundNumber = 1;
        await test.Db.SaveChangesAsync();
        return room;
    }
}
