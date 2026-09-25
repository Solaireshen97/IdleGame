using Game.Server.Data;
using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public class RoomServiceTests
{
    [Fact]
    public async Task CreateRoomAsync_ConfiguredEncounterPersistsEveryWaveAndSelectsFirstEnemy()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var progression = ProgressionTestFactory.Create();
        var encounters = new DungeonEncounterCatalog(Options.Create(new DungeonEncounterOptions
        {
            Dungeons = new Dictionary<string, List<DungeonWaveOptions>>(StringComparer.OrdinalIgnoreCase)
            {
                ["slime-field"] =
                [
                    new() { Monsters = [new() { Name = "Slime A", MaxHp = 30, Attack = 5, Defense = 1, RewardProfileCode = "slime-a" }] },
                    new() { Monsters = [new() { Name = "Slime B", MaxHp = 60, Attack = 9, Defense = 2, RewardProfileCode = "slime-b", IsBoss = true }] }
                ]
            }
        }));
        var service = new RoomService(test.Db,
            new UserService(test.Db, progression, SkillTestFactory.Create()), progression,
            ConsumableTestFactory.Create(), SkillTestFactory.Create(),
            RewardTestFactory.CreateService(test.Db, progression), encounters);

        var (detail, error) = await service.CreateRoomAsync("Slime", test.Token);

        Assert.Null(error);
        Assert.Equal((1, 2, "Slime A"), (detail!.CurrentWaveNumber, detail.TotalWaveCount, detail.MonsterName));
        var monsters = await test.Db.Monsters.OrderBy(monster => monster.WaveNumber).ToListAsync();
        Assert.Equal(2, monsters.Count);
        Assert.All(monsters, monster => Assert.Equal(detail.RoomId, monster.RoomId));
        Assert.Equal(new[] { 1, 2 }, monsters.Select(monster => monster.WaveNumber));
        Assert.Equal(new[] { "slime-a", "slime-b" }, monsters.Select(monster => monster.RewardProfileCode));
        Assert.True(monsters[1].IsBoss);
    }

    [Fact]
    public async Task CreateRoomAsync_StoresRepeatBattleChoice()
    {
        await using var test = await RoomTestContext.CreateAsync();

        var (detail, error) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isRepeatBattle: true);

        Assert.Null(error);
        Assert.True(detail!.IsRepeatBattle);
        Assert.True((await test.Db.Rooms.FindAsync(detail.RoomId))!.IsRepeatBattle);
    }

    [Fact]
    public async Task RepeatRoomDeadlineReleasesOnlyItsCharacterAndKeepsHistoryVisible()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (first, firstError) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isRepeatBattle: true);
        Assert.Null(firstError);
        Assert.NotNull(first!.ExpiresAtUtc);
        Assert.InRange(first.ExpiresAtUtc.Value - first.StartedAtUtc!.Value,
            TimeSpan.FromHours(12).Subtract(TimeSpan.FromSeconds(1)),
            TimeSpan.FromHours(12).Add(TimeSpan.FromSeconds(1)));
        var secondCharacter = await test.AddCharacterAsync("Mage");
        var user = await test.Db.Users.SingleAsync();
        user.ActiveCharacterId = secondCharacter.Id;
        await test.Db.SaveChangesAsync();
        var (second, secondError) = await test.Service.CreateRoomAsync(null, "Slime", test.Token);
        Assert.Null(secondError);
        Assert.Equal(2, await test.Db.CharacterActivities.CountAsync());

        var firstRoom = await test.Db.Rooms.FindAsync(first.RoomId);
        firstRoom!.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        var progression = ProgressionTestFactory.Create();
        var battle = new BattleService(test.Db,
            new UserService(test.Db, progression, SkillTestFactory.Create()),
            ConsumableTestFactory.Create(), SkillTestFactory.Create(),
            RewardTestFactory.CreateService(test.Db, progression));
        var (_, syncError) = await battle.SyncRoomAsync(first.RoomId);
        Assert.Null(syncError);
        Assert.NotNull(firstRoom.ClosedAtUtc);
        Assert.False(await test.Db.CharacterActivities.AnyAsync(activity => activity.CharacterId == test.ActiveCharacter.Id));
        Assert.True(await test.Db.CharacterActivities.AnyAsync(activity => activity.CharacterId == secondCharacter.Id));
        Assert.Contains(await test.Service.GetRoomsAsync(test.Token), room => room.RoomId == first.RoomId && room.ClosedAtUtc.HasValue);

        user.ActiveCharacterId = test.ActiveCharacter.Id;
        await test.Db.SaveChangesAsync();
        var (third, thirdError) = await test.Service.CreateRoomAsync(null, "Slime", test.Token);
        Assert.Null(thirdError);
        Assert.NotNull(third);
    }

    [Fact]
    public async Task CreateRoomAsync_InitializesDefaultDungeonsAndUsesSelectedDungeon()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (first, firstError) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var dungeons = await test.Db.Dungeons.OrderBy(dungeon => dungeon.SortOrder).ToListAsync();
        var (second, secondError) = await test.Service.CreateRoomAsync(dungeons.Single(dungeon => dungeon.Code == "goblin-camp").Id, null, test.Token);

        Assert.Null(firstError);
        Assert.Equal(69, dungeons.Count);
        Assert.Equal("northshire-wolves", dungeons[0].Code);
        Assert.Equal(66, dungeons.Count(dungeon => dungeon.IsVisible));
        Assert.False(dungeons.Single(dungeon => dungeon.Code == "slime-field").IsVisible);
        Assert.Equal(8, dungeons.Single(dungeon => dungeon.Code == "kobold-mine").MinimumLevel);
        Assert.Null(second);
        Assert.Equal("CharacterAlreadyInRoom", secondError);
        Assert.Equal(dungeons.Single(dungeon => dungeon.Code == "slime-field").Id, first!.DungeonId);
        Assert.Equal(ElementType.Wind, first.MonsterElement);
    }

    [Fact]
    public async Task DungeonList_ExposesProgressionAndCreateRoomEnforcesMinimumLevel()
    {
        await using var test = await RoomTestContext.CreateAsync();
        await DbInitializer.EnsureDefaultDungeonsAsync(test.Db);

        var dungeons = await test.Service.GetDungeonsAsync(test.Token);
        var mine = Assert.Single(dungeons, dungeon => dungeon.Code == "kobold-mine");
        var firstHunt = Assert.Single(dungeons, dungeon => dungeon.Code == "northshire-wolves");
        var (room, error) = await test.Service.CreateRoomAsync(mine.DungeonId, null, test.Token);

        Assert.Equal(66, dungeons.Count);
        Assert.True(firstHunt.CanEnter);
        Assert.Equal(100, firstHunt.ExperiencePercent);
        Assert.False(mine.CanEnter);
        Assert.Equal("需要角色达到 Lv.8", mine.LockReason);
        Assert.Null(room);
        Assert.Equal("CharacterLevelTooLow", error);
    }

    [Fact]
    public async Task DungeonList_ExposesExperienceReductionForLowerLevelContent()
    {
        await using var test = await RoomTestContext.CreateAsync();
        await DbInitializer.EnsureDefaultDungeonsAsync(test.Db);
        test.ActiveCharacter.Level = 3;
        await test.Db.SaveChangesAsync();

        var dungeons = await test.Service.GetDungeonsAsync(test.Token);

        Assert.Equal(40, dungeons.Single(dungeon => dungeon.Code == "northshire-wolves").ExperiencePercent);
        Assert.Equal(75, dungeons.Single(dungeon => dungeon.Code == "forest-spiders").ExperiencePercent);
        Assert.Equal(100, dungeons.Single(dungeon => dungeon.Code == "stone-tusk-boars").ExperiencePercent);
    }

    [Fact]
    public async Task DungeonList_SeparatesMonsterDropsAndExposesWeaponBaseStats()
    {
        await using var test = await RoomTestContext.CreateAsync();
        await DbInitializer.EnsureDefaultDungeonsAsync(test.Db);
        var dungeon = await test.Db.Dungeons.SingleAsync(item => item.Code == "slime-field");
        dungeon.IsVisible = true;
        await test.Db.SaveChangesAsync();

        var progression = ProgressionTestFactory.Create();
        var encounters = new DungeonEncounterCatalog(Options.Create(new DungeonEncounterOptions
        {
            Dungeons = new Dictionary<string, List<DungeonWaveOptions>>
            {
                ["slime-field"] = [new() { Monsters =
                [
                    new() { Name = "Wind Slime", Element = ElementType.Wind, MaxHp = 30, Attack = 5, Defense = 1, RewardProfileCode = "slime-field" },
                    new() { Name = "Fire Slime", Element = ElementType.Fire, MaxHp = 40, Attack = 6, Defense = 2, RewardProfileCode = "no-extra-drops" }
                ] }]
            }
        }));
        var service = new RoomService(test.Db,
            new UserService(test.Db, progression, SkillTestFactory.Create()), progression,
            ConsumableTestFactory.Create(), SkillTestFactory.Create(),
            RewardTestFactory.CreateService(test.Db, progression, guaranteedWeapon: true), encounters);

        var preview = await service.GetDungeonAsync(dungeon.Id, test.Token);

        Assert.NotNull(preview);
        Assert.Equal(2, preview.Monsters.Count);
        var first = preview.Monsters[0];
        Assert.Equal(("Wind Slime", ElementType.Wind, 30), (first.Name, first.Element, first.MaxHp));
        var weapon = Assert.Single(first.Drops).Weapon;
        Assert.NotNull(weapon);
        Assert.Equal((ElementType.Wind, 7, 28), (weapon.Element, weapon.Attack, weapon.MaxHp));
        Assert.Contains("10%", Assert.Single(weapon.Skills).Description);
        Assert.Equal("Fire Slime", preview.Monsters[1].Name);
        Assert.Empty(preview.Monsters[1].Drops);
    }

    [Fact]
    public async Task CreateRoomAsync_StoresPreparationTimeoutChoice()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (detail, error) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isPreparationTimeoutEnabled: false);

        Assert.Null(error);
        Assert.False(detail!.IsMixedTeam);
        Assert.False(detail.IsPreparationTimeoutEnabled);
        Assert.Null(detail.PreparationExpiresAtUtc);
        Assert.False((await test.Db.Rooms.FindAsync(detail.RoomId))!.IsPreparationTimeoutEnabled);
    }

    [Fact]
    public async Task CreateRoomAsync_WithPreparationTimeoutStartsConfiguredIdleCountdown()
    {
        await using var test = await RoomTestContext.CreateAsync();

        var (detail, error) = await test.Service.CreateRoomAsync("Slime", test.Token);

        Assert.Null(error);
        Assert.Equal(RoomStatus.NotStarted, detail!.RoomStatus);
        Assert.InRange((detail.PreparationExpiresAtUtc!.Value - detail.ServerTimeUtc).TotalSeconds,
            BattleRules.PreparationTimeoutSeconds - 1, BattleRules.PreparationTimeoutSeconds + 1);
    }

    [Fact]
    public async Task CreateRoomAsync_MixedTeamRespectsPreparationTimeoutChoice()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (room, _) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isPreparationTimeoutEnabled: false, isPublic: true);
        await test.AddOtherActiveCharacterAsync();
        var (detail, error) = await test.Service.JoinRoomAsync(room!.RoomId, new JoinRoomRequest { SlotIndex = 2 }, "other-token");

        Assert.Null(error);
        Assert.True(detail!.IsMixedTeam);
        Assert.False(detail.IsPreparationTimeoutEnabled);
    }

    [Fact]
    public async Task CreateRoomAsync_CreatesFiveSlotsAndMainControl()
    {
        await using var test = await RoomTestContext.CreateAsync();
        test.ActiveCharacter.Hp = 23;
        await test.Db.SaveChangesAsync();
        var (detail, error) = await test.Service.CreateRoomAsync("Slime", test.Token);

        Assert.Null(error);
        Assert.NotNull(detail);
        Assert.Equal(100, test.ActiveCharacter.Hp);
        Assert.Equal(5, detail!.Slots.Count);
        var firstSlot = Assert.Single(detail.Slots, x => x.SlotIndex == 1);
        Assert.Equal(test.ActiveCharacter.Id, firstSlot.CharacterId);
        Assert.True(firstSlot.IsMainControl);
    }

    [Fact]
    public async Task GetRoomsAsync_MarksOwnedAndJoinedRoomsForCurrentUser()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (room, _) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isPublic: true);
        await test.AddOtherActiveCharacterAsync();

        var ownerSummary = Assert.Single(await test.Service.GetRoomsAsync(test.Token));
        var guestBeforeJoin = Assert.Single(await test.Service.GetRoomsAsync("other-token"));
        await test.Service.JoinRoomAsync(room!.RoomId, new JoinRoomRequest { SlotIndex = 2 }, "other-token");
        var guestAfterJoin = Assert.Single(await test.Service.GetRoomsAsync("other-token"));

        Assert.True(ownerSummary.IsCurrentUserParticipant);
        Assert.True(ownerSummary.IsOwnedByCurrentUser);
        Assert.False(guestBeforeJoin.IsCurrentUserParticipant);
        Assert.False(guestBeforeJoin.IsOwnedByCurrentUser);
        Assert.True(guestAfterJoin.IsCurrentUserParticipant);
        Assert.False(guestAfterJoin.IsOwnedByCurrentUser);
    }

    [Fact]
    public async Task PrivateRoom_IsHiddenFromGuestsAndStillAllowsOwnersOtherCharacters()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, error) = await test.Service.CreateRoomAsync("Slime", test.Token);
        Assert.Null(error);
        Assert.False(created!.IsPublic);
        await test.AddOtherActiveCharacterAsync();

        Assert.Contains(await test.Service.GetRoomsAsync(test.Token), room => room.RoomId == created.RoomId && !room.IsPublic);
        Assert.DoesNotContain(await test.Service.GetRoomsAsync("other-token"), room => room.RoomId == created.RoomId);
        Assert.Null(await test.Service.GetRoomDetailAsync(created.RoomId, "other-token"));
        Assert.Null(await test.Service.GetRoomDetailAsync(created.RoomId));
        var (joined, joinError) = await test.Service.JoinRoomAsync(created.RoomId,
            new JoinRoomRequest { SlotIndex = 2 }, "other-token");
        Assert.Null(joined);
        Assert.Equal("RoomPrivate", joinError);

        var secondCharacter = await test.AddCharacterAsync("Mage");
        var (updated, assignError) = await test.Service.AssignSlotAsync(created.RoomId,
            new AssignRoomSlotRequest { SlotIndex = 2, CharacterId = secondCharacter.Id }, test.Token);
        Assert.Null(assignError);
        Assert.Equal(secondCharacter.Id, updated!.Slots.Single(slot => slot.SlotIndex == 2).CharacterId);
    }

    [Fact]
    public async Task AssignSlotAsync_AssignsOwnedCharacter()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (detail, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var character = await test.AddCharacterAsync("Mage");

        var (updated, error) = await test.Service.AssignSlotAsync(detail!.RoomId, new AssignRoomSlotRequest { SlotIndex = 2, CharacterId = character.Id }, test.Token);

        Assert.Null(error);
        Assert.Equal(character.Id, updated!.Slots.Single(x => x.SlotIndex == 2).CharacterId);
    }

    [Fact]
    public async Task AssignSlotAsync_RejectsOtherUsersCharacter()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (detail, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var otherCharacter = await test.AddOtherCharacterAsync();

        var (updated, error) = await test.Service.AssignSlotAsync(detail!.RoomId, new AssignRoomSlotRequest { SlotIndex = 2, CharacterId = otherCharacter.Id }, test.Token);

        Assert.Null(updated);
        Assert.Equal("NotCharacterOwner", error);
        Assert.Null((await test.Db.RoomSlots.SingleAsync(x => x.RoomId == detail.RoomId && x.SlotIndex == 2)).CharacterId);
    }

    [Fact]
    public async Task JoinRoomAsync_EmptySlot_AddsOtherUsersActiveCharacter()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (room, _) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isPublic: true);
        await test.AddOtherActiveCharacterAsync();
        var otherCharacter = await test.Db.Characters.FindAsync(2);
        otherCharacter!.Hp = 17;
        await test.Db.SaveChangesAsync();

        var (detail, error) = await test.Service.JoinRoomAsync(room!.RoomId, new JoinRoomRequest { SlotIndex = 2 }, "other-token");

        Assert.Null(error);
        var slot = detail!.Slots.Single(x => x.SlotIndex == 2);
        Assert.Equal(2, slot.CharacterId);
        Assert.Equal("other", slot.PlayerName);
        Assert.Equal(100, otherCharacter.Hp);
    }

    [Fact]
    public async Task LeavingOrDeletingRoom_RestoresParticipantHp()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (room, _) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isPublic: true);
        await test.AddOtherActiveCharacterAsync();
        await test.Service.JoinRoomAsync(room!.RoomId, new JoinRoomRequest { SlotIndex = 2 }, "other-token");
        var otherCharacter = await test.Db.Characters.FindAsync(2);
        test.ActiveCharacter.Hp = 11;
        otherCharacter!.Hp = 0;
        await test.Db.SaveChangesAsync();

        var (_, leaveError) = await test.Service.LeaveRoomAsync(room.RoomId, "other-token");
        var (deleted, deleteError) = await test.Service.DeleteRoomAsync(room.RoomId, test.Token);

        Assert.Null(leaveError);
        Assert.True(deleted);
        Assert.Null(deleteError);
        Assert.Equal(100, otherCharacter.Hp);
        Assert.Equal(100, test.ActiveCharacter.Hp);
    }

    [Fact]
    public async Task JoinRoomAsync_OccupiedOrFinishedRoom_IsRejected()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (room, _) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isPublic: true);
        await test.AddOtherActiveCharacterAsync();

        var (_, occupiedError) = await test.Service.JoinRoomAsync(room!.RoomId, new JoinRoomRequest { SlotIndex = 1 }, "other-token");
        var entity = await test.Db.Rooms.FindAsync(room.RoomId);
        entity!.Status = RoomStatus.BattleOver;
        await test.Db.SaveChangesAsync();
        var (_, lockedError) = await test.Service.JoinRoomAsync(room.RoomId, new JoinRoomRequest { SlotIndex = 2 }, "other-token");

        Assert.Equal("SlotOccupied", occupiedError);
        Assert.Equal("BattleOver", lockedError);
    }

    [Fact]
    public async Task JoiningDuringAutoCooldownSwitchesToManualRoundTiming()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isPublic: true);
        await test.AddOtherActiveCharacterAsync();
        var room = await test.Db.Rooms.FindAsync(created!.RoomId);
        room!.Status = RoomStatus.Cooldown;
        room.RoundNumber = 1;
        room.RoundCooldownDurationSeconds = BattleRules.AutoRoundCooldownSeconds;
        room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(15);
        await test.Db.SaveChangesAsync();

        var (joined, error) = await test.Service.JoinRoomAsync(room.Id,
            new JoinRoomRequest { SlotIndex = 2 }, "other-token");

        Assert.Null(error);
        Assert.Equal(BattleRules.RoundCooldownSeconds, joined!.RoundCooldownDurationSeconds);
        Assert.InRange((joined.NextRoundAvailableAtUtc!.Value - joined.ServerTimeUtc).TotalSeconds, 4, 6);
        Assert.Equal(RoomStatus.Cooldown, joined.RoomStatus);
        Assert.NotNull((await test.Db.RoomSlots.SingleAsync(slot => slot.RoomId == room.Id && slot.SlotIndex == 2)).LastSeenAtUtc);
    }

    [Fact]
    public async Task StaleConcurrentJoinReturnsConflictWithoutOccupyingAnotherSlot()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isPublic: true);
        await test.AddOtherActiveCharacterAsync();
        test.Db.AddRange(new User { Id = 3, UserName = "third", PasswordHash = "x", ActiveCharacterId = 3 },
            new Character { Id = 3, UserId = 3, Name = "Third", Hp = 100, MaxHp = 100, Attack = 20 },
            new UserLoginSession { UserId = 3, Token = "third-token",
                CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
        await test.Db.SaveChangesAsync();
        await using var staleDb = test.CreateDbContext();
        await staleDb.Rooms.SingleAsync(room => room.Id == created!.RoomId);
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        var staleService = new RoomService(staleDb,
            new UserService(staleDb, progression, skills), progression,
            ConsumableTestFactory.Create(), skills, RewardTestFactory.CreateService(staleDb, progression));

        Assert.Null((await test.Service.JoinRoomAsync(created!.RoomId,
            new JoinRoomRequest { SlotIndex = 2 }, "other-token")).Error);
        var (_, error) = await staleService.JoinRoomAsync(created.RoomId,
            new JoinRoomRequest { SlotIndex = 3 }, "third-token");

        Assert.Equal("ConcurrencyConflict", error);
        Assert.Null((await test.Db.RoomSlots.SingleAsync(slot =>
            slot.RoomId == created.RoomId && slot.SlotIndex == 3)).CharacterId);
        Assert.False(await test.Db.CharacterActivities.AnyAsync(activity => activity.CharacterId == 3));
    }

    [Fact]
    public async Task GuestCanJoinPreparingRoomAndGetsFullPreparationWindow()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isPublic: true);
        await test.AddOtherActiveCharacterAsync();
        var room = await test.Db.Rooms.FindAsync(created!.RoomId);
        room!.Status = RoomStatus.Preparing;
        room.PreparationStartedAtUtc = DateTime.UtcNow.AddSeconds(-25);
        var ownerSlot = await test.Db.RoomSlots.SingleAsync(slot => slot.RoomId == room.Id && slot.SlotIndex == 1);
        ownerSlot.IsConfirmed = true;
        await test.Db.SaveChangesAsync();

        var (joined, joinError) = await test.Service.JoinRoomAsync(room.Id,
            new JoinRoomRequest { SlotIndex = 2 }, "other-token");
        Assert.Null(joinError);
        Assert.Equal(RoomStatus.Preparing, joined!.RoomStatus);
        Assert.InRange((joined.PreparationExpiresAtUtc!.Value - joined.ServerTimeUtc).TotalSeconds,
            BattleRules.PreparationTimeoutSeconds - 1, BattleRules.PreparationTimeoutSeconds + 1);
        Assert.False(joined.Slots.Single(slot => slot.SlotIndex == 2).IsConfirmed);

        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        var battle = new BattleService(test.Db, new UserService(test.Db, progression, skills),
            ConsumableTestFactory.Create(), skills, RewardTestFactory.CreateService(test.Db, progression));
        var (round, error) = await battle.StartPreparationAsync(room.Id, "other-token", 0);
        Assert.Null(error);
        Assert.NotNull(round);
        Assert.Equal(1, room.RoundNumber);
    }

    [Fact]
    public async Task AssignSlotAsync_DuringCooldown_DoesNotChangeSlots()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (detail, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var character = await test.AddCharacterAsync("Mage");
        var room = await test.Db.Rooms.FindAsync(detail!.RoomId);
        room!.Status = RoomStatus.Cooldown;
        await test.Db.SaveChangesAsync();

        var (updated, error) = await test.Service.AssignSlotAsync(detail.RoomId, new AssignRoomSlotRequest { SlotIndex = 2, CharacterId = character.Id }, test.Token);

        Assert.Null(updated);
        Assert.Equal("FormationLocked", error);
        Assert.Null((await test.Db.RoomSlots.SingleAsync(x => x.RoomId == detail.RoomId && x.SlotIndex == 2)).CharacterId);
    }

    [Fact]
    public async Task SetRoomVisibilityAsync_UpdatesDiscoveryAndAdmissionWithoutRemovingMembers()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var roomId = created!.RoomId;
        await test.AddOtherActiveCharacterAsync();

        var (opened, openError) = await test.Service.SetRoomVisibilityAsync(roomId, true, test.Token);
        Assert.Null(openError);
        Assert.True(opened!.IsPublic);
        Assert.Contains(await test.Service.GetRoomsAsync("other-token"), item => item.RoomId == roomId);
        var (joined, joinError) = await test.Service.JoinRoomAsync(roomId, new JoinRoomRequest { SlotIndex = 2 }, "other-token");
        Assert.Null(joinError);
        Assert.True(joined!.Slots.Single(slot => slot.SlotIndex == 2).IsOccupied);

        test.Db.AddRange(
            new User { Id = 3, UserName = "visitor", PasswordHash = "x", ActiveCharacterId = 3 },
            new Character { Id = 3, UserId = 3, Name = "Visitor", Hp = 100, MaxHp = 100, Attack = 20 },
            new UserLoginSession { UserId = 3, Token = "visitor-token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
        await test.Db.SaveChangesAsync();
        var (closed, closeError) = await test.Service.SetRoomVisibilityAsync(roomId, false, test.Token);
        Assert.Null(closeError);
        Assert.False(closed!.IsPublic);
        Assert.Equal(2, closed.Slots.Count(slot => slot.IsOccupied));
        Assert.Equal(2, await test.Db.CharacterActivities.CountAsync());
        Assert.NotNull(await test.Service.GetRoomDetailAsync(roomId, "other-token"));
        Assert.Contains(await test.Service.GetRoomsAsync("other-token"), item => item.RoomId == roomId && !item.IsPublic);
        Assert.DoesNotContain(await test.Service.GetRoomsAsync("visitor-token"), item => item.RoomId == roomId);
        Assert.Null(await test.Service.GetRoomDetailAsync(roomId, "visitor-token"));
        var (denied, deniedError) = await test.Service.JoinRoomAsync(roomId, new JoinRoomRequest { SlotIndex = 3 }, "visitor-token");
        Assert.Null(denied);
        Assert.Equal("RoomPrivate", deniedError);

        await test.Service.SetRoomVisibilityAsync(roomId, true, test.Token);
        var (reopened, rejoinError) = await test.Service.JoinRoomAsync(roomId, new JoinRoomRequest { SlotIndex = 3 }, "visitor-token");
        Assert.Null(rejoinError);
        Assert.Equal(3, reopened!.Slots.Count(slot => slot.IsOccupied));
    }

    [Theory]
    [InlineData(RoomStatus.NotStarted)]
    [InlineData(RoomStatus.Preparing)]
    [InlineData(RoomStatus.Cooldown)]
    [InlineData(RoomStatus.WaveTransition)]
    [InlineData(RoomStatus.BattleOver)]
    public async Task SetRoomVisibilityAsync_WorksDuringCombatAndPreservesRoundState(RoomStatus status)
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var room = (await test.Db.Rooms.FindAsync(created!.RoomId))!;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        room.Status = status;
        room.RoundNumber = 4;
        room.NextRoundAvailableAtUtc = deadline;
        room.PreparationStartedAtUtc = deadline.AddSeconds(-20);
        await test.Db.SaveChangesAsync();

        var (_, error) = await test.Service.SetRoomVisibilityAsync(room.Id, true, test.Token);
        Assert.Null(error);
        await test.Db.Entry(room).ReloadAsync();
        Assert.True(room.IsPublic);
        Assert.Equal(status, room.Status);
        Assert.Equal(4, room.RoundNumber);
        Assert.Equal(deadline, room.NextRoundAvailableAtUtc);
        Assert.Equal(deadline.AddSeconds(-20), room.PreparationStartedAtUtc);
    }

    [Fact]
    public async Task SetRoomVisibilityAsync_RejectsUnauthenticatedUsersAndNonOwners()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isPublic: true);
        await test.AddOtherActiveCharacterAsync();
        await test.Service.JoinRoomAsync(created!.RoomId, new JoinRoomRequest { SlotIndex = 2 }, "other-token");

        var (anonymous, anonymousError) = await test.Service.SetRoomVisibilityAsync(created.RoomId, false, null);
        Assert.Null(anonymous);
        Assert.Equal("Unauthorized", anonymousError);
        var (member, memberError) = await test.Service.SetRoomVisibilityAsync(created.RoomId, false, "other-token");
        Assert.Null(member);
        Assert.Equal("NotOwner", memberError);
        Assert.True((await test.Db.Rooms.FindAsync(created.RoomId))!.IsPublic);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetRoomVisibilityAsync_RejectsClosedOrExpiredRooms(bool expired)
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isRepeatBattle: true);
        var room = (await test.Db.Rooms.FindAsync(created!.RoomId))!;
        if (expired) room.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
        else room.ClosedAtUtc = DateTime.UtcNow;
        await test.Db.SaveChangesAsync();

        var (detail, error) = await test.Service.SetRoomVisibilityAsync(room.Id, true, test.Token);
        Assert.Null(detail);
        Assert.Equal("RoomClosed", error);
        Assert.False(room.IsPublic);
    }

    [Fact]
    public async Task SetRoomVisibilityAsync_DetectsConcurrentRoomChanges()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        await using var concurrentDb = test.CreateDbContext();
        var concurrentRoom = (await concurrentDb.Rooms.FindAsync(created!.RoomId))!;
        concurrentRoom.Status = RoomStatus.Cooldown;
        concurrentRoom.Version++;
        await concurrentDb.SaveChangesAsync();

        var (detail, error) = await test.Service.SetRoomVisibilityAsync(created.RoomId, true, test.Token);
        Assert.Null(detail);
        Assert.Equal("ConcurrencyConflict", error);
        await concurrentDb.Entry(concurrentRoom).ReloadAsync();
        Assert.False(concurrentRoom.IsPublic);
        Assert.Equal(RoomStatus.Cooldown, concurrentRoom.Status);
    }

    private sealed class RoomTestContext : IAsyncDisposable
    {
        private readonly string _path;
        private RoomTestContext(string path, GameDbContext db, Character activeCharacter)
        {
            _path = path;
            Db = db;
            ActiveCharacter = activeCharacter;
            var progression = ProgressionTestFactory.Create();
            Service = new RoomService(db, new UserService(db, progression, SkillTestFactory.Create()), progression, ConsumableTestFactory.Create(), SkillTestFactory.Create(), RewardTestFactory.CreateService(db, progression));
        }

        public string Token => "token";
        public GameDbContext Db { get; }
        public Character ActiveCharacter { get; }
        public RoomService Service { get; }

        public GameDbContext CreateDbContext() => new(new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite($"Data Source={_path};Pooling=False").Options);

        public static async Task<RoomTestContext> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"idlegame-room-tests-{Guid.NewGuid():N}.db");
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
            await db.Database.EnsureCreatedAsync();
            var activeCharacter = new Character { Id = 1, UserId = 1, Name = "Knight", Hp = 100, MaxHp = 100, Attack = 20};
            db.AddRange(new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 }, activeCharacter, new UserLoginSession { UserId = 1, Token = "token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();
            return new RoomTestContext(path, db, activeCharacter);
        }

        public async Task<Character> AddCharacterAsync(string name)
        {
            var character = new Character { UserId = 1, Name = name, Hp = 100, MaxHp = 100, Attack = 20};
            Db.Characters.Add(character);
            await Db.SaveChangesAsync();
            return character;
        }

        public async Task AddOtherActiveCharacterAsync()
        {
            Db.AddRange(
                new User { Id = 2, UserName = "other", PasswordHash = "x", ActiveCharacterId = 2 },
                new Character { Id = 2, UserId = 2, Name = "Other", Hp = 100, MaxHp = 100, Attack = 20},
                new UserLoginSession { UserId = 2, Token = "other-token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            await Db.SaveChangesAsync();
        }

        public async Task<Character> AddOtherCharacterAsync()
        {
            var character = new Character { UserId = 2, Name = "Other", Hp = 100, MaxHp = 100, Attack = 20};
            Db.AddRange(new User { Id = 2, UserName = "other", PasswordHash = "x", ActiveCharacterId = null }, character);
            await Db.SaveChangesAsync();
            return character;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            File.Delete(_path);
        }
    }
}
