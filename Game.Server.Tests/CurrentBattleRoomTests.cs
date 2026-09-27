using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Xunit;

namespace Game.Server.Tests;

public partial class RoomServiceTests
{
    [Theory]
    [InlineData(RoomStatus.NotStarted)]
    [InlineData(RoomStatus.Preparing)]
    [InlineData(RoomStatus.Cooldown)]
    [InlineData(RoomStatus.WaveTransition)]
    [InlineData(RoomStatus.BattleOver)]
    public async Task CurrentCharacterReturnsActiveBattleRoomThroughoutRepeatBattle(RoomStatus status)
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, error) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isRepeatBattle: true);
        Assert.Null(error);
        var room = (await test.Db.Rooms.FindAsync(created!.RoomId))!;
        room.Status = status;
        await test.Db.SaveChangesAsync();
        var users = new UserService(test.Db, ProgressionTestFactory.Create(), SkillTestFactory.Create());

        var (current, currentError) = await users.GetCurrentCharacterAsync(test.Token);

        Assert.Null(currentError);
        Assert.Equal(created.RoomId, current!.ActiveBattleRoomId);
    }

    [Fact]
    public async Task CurrentBattleRoomFollowsSelectedCharacterInsteadOfAnotherOwnedRoom()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var users = new UserService(test.Db, ProgressionTestFactory.Create(), SkillTestFactory.Create());
        var (firstRoom, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var second = await test.AddCharacterAsync("Mage");

        Assert.Null((await users.SelectCurrentCharacterAsync(test.Token, second.Id)).Error);
        var (idle, idleError) = await users.GetCurrentCharacterAsync(test.Token);
        Assert.Null(idleError);
        Assert.Null(idle!.ActiveBattleRoomId);

        var (secondRoom, createError) = await test.Service.CreateRoomAsync("Slime", test.Token);
        Assert.Null(createError);
        Assert.NotEqual(firstRoom!.RoomId, secondRoom!.RoomId);
        Assert.Equal(secondRoom.RoomId, (await users.GetCurrentCharacterAsync(test.Token)).Response!.ActiveBattleRoomId);

        Assert.Null((await users.SelectCurrentCharacterAsync(test.Token, test.ActiveCharacter.Id)).Error);
        Assert.Equal(firstRoom.RoomId, (await users.GetCurrentCharacterAsync(test.Token)).Response!.ActiveBattleRoomId);
    }

    [Fact]
    public async Task CurrentBattleRoomIncludesJoinedRoomAndClearsAfterLeaving()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isPublic: true);
        await test.AddOtherActiveCharacterAsync();
        Assert.Null((await test.Service.JoinRoomAsync(created!.RoomId,
            new JoinRoomRequest { SlotIndex = 2 }, "other-token")).Error);
        var users = new UserService(test.Db, ProgressionTestFactory.Create(), SkillTestFactory.Create());

        Assert.Equal(created.RoomId, (await users.GetCurrentCharacterAsync("other-token")).Response!.ActiveBattleRoomId);
        Assert.Null((await test.Service.LeaveRoomAsync(created.RoomId, "other-token")).Error);
        Assert.Null((await users.GetCurrentCharacterAsync("other-token")).Response!.ActiveBattleRoomId);
    }

    [Fact]
    public async Task CurrentBattleRoomExcludesClosedRoomEvenWhenSlotStillReferencesCharacter()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var room = (await test.Db.Rooms.FindAsync(created!.RoomId))!;
        room.ClosedAtUtc = DateTime.UtcNow;
        await test.Db.SaveChangesAsync();
        var users = new UserService(test.Db, ProgressionTestFactory.Create(), SkillTestFactory.Create());

        var (current, error) = await users.GetCurrentCharacterAsync(test.Token);

        Assert.Null(error);
        Assert.Null(current!.ActiveBattleRoomId);
    }
}
