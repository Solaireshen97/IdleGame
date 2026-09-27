using Game.Server.Services;
using Game.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public partial class RoomServiceTests
{
    [Fact]
    public async Task QuickSkillCastPreferenceFollowsCharacterAcrossRoomsAndNewSessions()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var second = await test.AddCharacterAsync("Priest");
        var users = new UserService(test.Db, ProgressionTestFactory.Create(), SkillTestFactory.Create());
        Assert.True((await users.SetQuickSkillCastAsync(test.Token, test.ActiveCharacter.Id, true)).Success);
        var (firstRoom, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        await test.Service.AssignSlotAsync(firstRoom!.RoomId,
            new AssignRoomSlotRequest { SlotIndex = 5, CharacterId = second.Id }, test.Token);
        var detail = await test.Service.GetRoomDetailAsync(firstRoom.RoomId, test.Token);
        Assert.True(detail!.Slots.Single(slot => slot.CharacterId == test.ActiveCharacter.Id).IsQuickSkillCastEnabled);
        Assert.False(detail.Slots.Single(slot => slot.CharacterId == second.Id).IsQuickSkillCastEnabled);
        await test.Service.DeleteRoomAsync(firstRoom.RoomId, test.Token);

        await using var freshDb = test.CreateDbContext();
        var service = MakeRoomService(freshDb);
        var (newRoom, error) = await service.CreateRoomAsync("Slime", test.Token);
        Assert.Null(error);
        Assert.True(newRoom!.Slots.Single(slot => slot.CharacterId == test.ActiveCharacter.Id).IsQuickSkillCastEnabled);
        var freshUsers = new UserService(freshDb, ProgressionTestFactory.Create(), SkillTestFactory.Create());
        Assert.True((await freshUsers.SetQuickSkillCastAsync(test.Token, test.ActiveCharacter.Id, false)).Success);
        Assert.False((await service.GetRoomDetailAsync(newRoom.RoomId, test.Token))!.Slots
            .Single(slot => slot.CharacterId == test.ActiveCharacter.Id).IsQuickSkillCastEnabled);
    }

    [Fact]
    public async Task QuickSkillCastPreferenceCannotBeChangedByAnotherAccount()
    {
        await using var test = await RoomTestContext.CreateAsync();
        await test.AddOtherActiveCharacterAsync();
        var users = new UserService(test.Db, ProgressionTestFactory.Create(), SkillTestFactory.Create());
        Assert.Equal("Unauthorized", (await users.SetQuickSkillCastAsync(null, 1, true)).Error);
        Assert.Equal("NotOwner", (await users.SetQuickSkillCastAsync("other-token", 1, true)).Error);
        Assert.Equal("CharacterNotFound", (await users.SetQuickSkillCastAsync(test.Token, 999, true)).Error);
        Assert.False((await test.Db.Characters.AsNoTracking().SingleAsync(character => character.Id == 1)).IsQuickSkillCastEnabled);
    }

    [Fact]
    public async Task SavingPreferenceDuringBattleDoesNotOverwriteOrInvalidateRoundChanges()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var room = await CreateRunningRoom(test);
        var character = test.ActiveCharacter;
        var version = character.Version;
        character.Hp = 28; character.Gold = 83;
        // Another request updates the preference while the battle context has unsaved results.
        await using var otherDb = test.CreateDbContext();
        var users = new UserService(otherDb, ProgressionTestFactory.Create(), SkillTestFactory.Create());
        Assert.True((await users.SetQuickSkillCastAsync(test.Token, character.Id, true)).Success);
        Assert.Equal(version, (await otherDb.Characters.AsNoTracking().SingleAsync(item => item.Id == character.Id)).Version);
        character.Version++;
        await test.Db.SaveChangesAsync();
        await using var freshDb = test.CreateDbContext();
        var saved = await freshDb.Characters.SingleAsync(item => item.Id == character.Id);
        Assert.True(saved.IsQuickSkillCastEnabled);
        Assert.Equal((28, 83), (saved.Hp, saved.Gold));
        Assert.Equal(room.Id, (await freshDb.CharacterActivities.SingleAsync()).SourceId);
        Assert.False((await freshDb.Rooms.FindAsync(room.Id))!.IsOwnerAutoEnabled);
    }
}
