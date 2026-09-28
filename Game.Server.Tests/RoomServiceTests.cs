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

public partial class RoomServiceTests
{
    [Fact]
    public async Task CumulativeRewardsIncludeAllRoomRunsAndKeepSameNamedCharactersSeparate()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isPublic: true);
        await test.AddOtherActiveCharacterAsync();
        var second = await test.AddCharacterAsync(test.ActiveCharacter.Name);
        var retired = await test.AddCharacterAsync("Retired");
        var (_, assignError) = await test.Service.AssignSlotAsync(created!.RoomId,
            new AssignRoomSlotRequest { SlotIndex = 3, CharacterId = second.Id }, test.Token);
        Assert.Null(assignError);
        var room = (await test.Db.Rooms.FindAsync(created.RoomId))!;
        room.RunSequence = 3;
        room.Status = RoomStatus.Cooldown;
        test.Db.RewardRuns.AddRange(
            new RewardRun { RoomId = room.Id, Sequence = 1, Status = "Victory" },
            new RewardRun { RoomId = room.Id, Sequence = 2, Status = "Defeat" },
            new RewardRun { RoomId = room.Id, Sequence = 3 },
            new RewardRun { RoomId = room.Id, Sequence = 4, Status = "Victory" });
        AddRewards(test.Db, room.Id, 1, 1, test.ActiveCharacter.Id, 10, 8, 1);
        AddRewards(test.Db, room.Id, 2, 1, test.ActiveCharacter.Id, 7, 5, 2);
        AddRewards(test.Db, room.Id, 3, 1, test.ActiveCharacter.Id, 3, 2, 4);
        AddRewards(test.Db, room.Id, 1, 1, second.Id, 6, 9, 5);
        AddRewards(test.Db, room.Id, 2, 1, retired.Id, 2, 1, 1);
        AddRewards(test.Db, room.Id, 3, 2, 2, 999, 999, 999);
        AddRewards(test.Db, room.Id, 4, 1, test.ActiveCharacter.Id, 999, 999, 999);
        AddRewards(test.Db, room.Id + 100, 1, 1, test.ActiveCharacter.Id, 999, 999, 999);
        await test.Db.SaveChangesAsync();

        var detail = await test.Service.GetRoomDetailAsync(room.Id, test.Token);

        Assert.Null(detail!.ClosedAtUtc);
        var rewards = Assert.IsType<RoomCumulativeRewardsResponse>(detail.CumulativeRewards);
        Assert.Equal((2, 28, 25), (rewards.CompletedRuns, rewards.Gold, rewards.Experience));
        Assert.True(rewards.HasPendingRewards);
        Assert.Equal(3, rewards.Characters.Count);
        var first = Assert.Single(rewards.Characters, character => character.CharacterId == test.ActiveCharacter.Id);
        Assert.Equal((20, 15, 3, 2), (first.Gold, first.Experience, first.PendingGold, first.PendingExperience));
        var potion = Assert.Single(first.Items);
        Assert.Equal(("minor-healing-potion", 7, 4), (potion.Code, potion.Quantity, potion.PendingQuantity));
        var secondRewards = Assert.Single(rewards.Characters, character => character.CharacterId == second.Id);
        Assert.Equal(first.CharacterName, secondRewards.CharacterName);
        Assert.Equal((6, 9, 5, 0), (secondRewards.Gold, secondRewards.Experience,
            Assert.Single(secondRewards.Items).Quantity, secondRewards.PendingGold));
        Assert.Contains(rewards.Characters, character => character.CharacterId == retired.Id);
        Assert.DoesNotContain(rewards.Characters, character => character.CharacterId == 2);
        Assert.Equal(13, rewards.Items.Sum(item => item.Quantity));
        Assert.Equal(3, detail.Rewards!.Gold);
        Assert.Equal(3, detail.Rewards.RunSequence);

        var guest = await test.Service.GetRoomDetailAsync(room.Id, "other-token");
        var guestCharacter = Assert.Single(guest!.CumulativeRewards!.Characters);
        Assert.Equal(2, guestCharacter.CharacterId);
        Assert.Equal(999, guest.CumulativeRewards.Gold);

        var refreshed = await test.Service.GetRoomDetailAsync(room.Id, test.Token);
        Assert.Equal(rewards.Gold, refreshed!.CumulativeRewards!.Gold);
        Assert.Equal(potion.Quantity, refreshed.CumulativeRewards.Items.Single(item => item.CharacterId == first.CharacterId).Quantity);
    }

    [Fact]
    public async Task CumulativeRewardsPersistOnNewRunAndAfterRoomClosesAndSlotsAreReleased()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var room = (await test.Db.Rooms.FindAsync(created!.RoomId))!;
        room.RunSequence = 2;
        test.Db.RewardRuns.Add(new RewardRun { RoomId = room.Id, Sequence = 1, Status = "Victory" });
        AddRewards(test.Db, room.Id, 1, 1, test.ActiveCharacter.Id, 10, 8, 2);
        await test.Db.SaveChangesAsync();

        var active = (await test.Service.GetRoomDetailAsync(room.Id, test.Token))!.CumulativeRewards!;
        Assert.Equal((1, 10, 8), (active.CompletedRuns, active.Gold, active.Experience));
        Assert.False(active.HasPendingRewards);
        Assert.Equal(2, Assert.Single(Assert.Single(active.Characters).Items).Quantity);

        await CharacterActivityManager.CloseBattleRoomAsync(test.Db, room, DateTime.UtcNow);
        await test.Db.SaveChangesAsync();
        Assert.All(await test.Db.RoomSlots.Where(slot => slot.RoomId == room.Id).ToListAsync(), slot => Assert.Null(slot.CharacterId));
        var closed = (await test.Service.GetRoomDetailAsync(room.Id, test.Token))!.CumulativeRewards!;
        Assert.Equal((active.CompletedRuns, active.Gold, active.Experience), (closed.CompletedRuns, closed.Gold, closed.Experience));
        Assert.Equal(test.ActiveCharacter.Id, Assert.Single(closed.Characters).CharacterId);
        Assert.Equal(2, Assert.Single(closed.Items).Quantity);
    }

    [Fact]
    public async Task CumulativeRewardsKeepDifferentItemKindsSeparateAndPendingDropsBecomeSettled()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        test.Db.RewardRuns.Add(new RewardRun { RoomId = created!.RoomId, Sequence = 1 });
        test.Db.RewardEntries.AddRange(
            new RewardEntry { RoomId = created.RoomId, Sequence = 1, UserId = 1, CharacterId = 1, Kind = "Material", Code = "shared-name", Quantity = 2 },
            new RewardEntry { RoomId = created.RoomId, Sequence = 1, UserId = 1, CharacterId = 1, Kind = "Consumable", Code = "shared-name", Quantity = 3 });
        await test.Db.SaveChangesAsync();
        var pending = (await test.Service.GetRoomDetailAsync(created.RoomId, test.Token))!.CumulativeRewards!;
        Assert.True(pending.HasPendingRewards);
        Assert.Equal(2, pending.Items.Count);
        Assert.All(pending.Items, item => Assert.Equal(item.Quantity, item.PendingQuantity));

        var run = await test.Db.RewardRuns.SingleAsync();
        run.Status = "Victory";
        run.SettledAtUtc = DateTime.UtcNow;
        await test.Db.SaveChangesAsync();
        var settled = (await test.Service.GetRoomDetailAsync(created.RoomId, test.Token))!.CumulativeRewards!;
        Assert.False(settled.HasPendingRewards);
        Assert.Equal(1, settled.CompletedRuns);
        Assert.Equal(5, settled.Items.Sum(item => item.Quantity));
        Assert.All(settled.Items, item => Assert.Equal(0, item.PendingQuantity));
    }

    [Fact]
    public async Task CumulativeRewardsAreAvailableBeforeFirstDropWithAnEmptyCharacterSummary()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, error) = await test.Service.CreateRoomAsync("Slime", test.Token);
        Assert.Null(error);
        var rewards = Assert.IsType<RoomCumulativeRewardsResponse>(created!.CumulativeRewards);
        Assert.Equal((0, 0, 0), (rewards.CompletedRuns, rewards.Gold, rewards.Experience));
        Assert.False(rewards.HasPendingRewards);
        var character = Assert.Single(rewards.Characters);
        Assert.Equal(test.ActiveCharacter.Id, character.CharacterId);
        Assert.Empty(character.Items);
    }

    private static void AddRewards(GameDbContext db, int roomId, int sequence, int userId, int characterId,
        int gold, int experience, int potions)
    {
        db.RewardEntries.AddRange(
            new RewardEntry { RoomId = roomId, Sequence = sequence, UserId = userId, CharacterId = characterId, Kind = "Gold", Quantity = gold },
            new RewardEntry { RoomId = roomId, Sequence = sequence, UserId = userId, CharacterId = characterId, Kind = "Experience", Quantity = experience },
            new RewardEntry { RoomId = roomId, Sequence = sequence, UserId = userId, CharacterId = characterId, Kind = "Consumable", Code = "minor-healing-potion", Quantity = potions });
    }

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
        Assert.Equal(30, dungeons.Count(dungeon => dungeon.IsVisible));
        Assert.False(dungeons.Single(dungeon => dungeon.Code == "slime-field").IsVisible);
        Assert.Equal(8, dungeons.Single(dungeon => dungeon.Code == "kobold-mine").MinimumLevel);
        Assert.Null(second);
        Assert.Equal("CharacterAlreadyInRoom", secondError);
        Assert.Equal(dungeons.Single(dungeon => dungeon.Code == "slime-field").Id, first!.DungeonId);
        Assert.Equal(ElementType.Wind, first.MonsterElement);
    }

    [Fact]
    public async Task DungeonList_ExposesRecommendationAndAllowsLowLevelRoomCreation()
    {
        await using var test = await RoomTestContext.CreateAsync();
        await DbInitializer.EnsureDefaultDungeonsAsync(test.Db);

        var dungeons = await test.Service.GetDungeonsAsync(test.Token);
        var mine = Assert.Single(dungeons, dungeon => dungeon.Code == "kobold-mine");
        var firstHunt = Assert.Single(dungeons, dungeon => dungeon.Code == "northshire-wolves");
        var (room, error) = await test.Service.CreateRoomAsync(mine.DungeonId, null, test.Token);

        Assert.Equal(30, dungeons.Count);
        Assert.True(firstHunt.CanEnter);
        Assert.Equal(100, firstHunt.ExperiencePercent);
        Assert.True(mine.CanEnter);
        Assert.Equal(10, mine.RecommendedLevel);
        Assert.Null(mine.LockReason);
        Assert.NotNull(room);
        Assert.Null(error);
    }

    [Fact]
    public async Task DungeonList_ExposesExperienceReductionForLowerLevelContent()
    {
        await using var test = await RoomTestContext.CreateAsync();
        await DbInitializer.EnsureDefaultDungeonsAsync(test.Db);
        test.ActiveCharacter.Level = 3;
        await test.Db.SaveChangesAsync();

        var dungeons = await test.Service.GetDungeonsAsync(test.Token);

        Assert.Equal(100, dungeons.Single(dungeon => dungeon.Code == "northshire-wolves").ExperiencePercent);
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
    public async Task CreateRoomAsync_CreatesFiveSlotsWithTheAccountSelectedCharacter()
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
        Assert.False(detail.IsCurrentUserAutoEnabled);
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
    public async Task AssignSlotAsync_MovesCharacterToEmptySlotWithItsStateAndActivity()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var room = (await test.Db.Rooms.FindAsync(created!.RoomId))!;
        room.Status = RoomStatus.BattleOver;
        room.RoundNumber = 3;
        var source = await test.Db.RoomSlots.SingleAsync(slot => slot.RoomId == room.Id && slot.SlotIndex == 1);
        var lastSeen = DateTime.UtcNow.AddSeconds(-5);
        source.LastSeenAtUtc = lastSeen;
        source.IsAutoEnabled = true;
        room.IsOwnerAutoEnabled = true;
        source.IsConfirmed = true;
        source.IsTemporaryAuto = true;
        source.PendingConsumableSlotMask = 2;
        source.PendingSkillSlotMask = 5;
        source.IsSoulImprintQueued = true;
        source.HasParticipatedInRun = true;
        source.LastParticipatedMonsterId = room.MonsterId;
        test.ActiveCharacter.Hp = 17;
        await test.Db.SaveChangesAsync();
        var activity = await test.Db.CharacterActivities.AsNoTracking().SingleAsync();
        var version = room.Version;

        var (updated, error) = await test.Service.AssignSlotAsync(room.Id,
            new AssignRoomSlotRequest { SlotIndex = 5, CharacterId = test.ActiveCharacter.Id }, test.Token);

        Assert.Null(error);
        Assert.Equal(5, Assert.Single(updated!.Slots, slot => slot.IsOccupied).SlotIndex);
        Assert.True(updated.IsCurrentUserAutoEnabled);
        await using var persisted = test.CreateDbContext();
        var empty = await persisted.RoomSlots.SingleAsync(slot => slot.RoomId == room.Id && slot.SlotIndex == 1);
        Assert.Null(empty.CharacterId);
        Assert.Null(empty.UserId);
        Assert.Null(empty.LastSeenAtUtc);
        Assert.False(empty.IsAutoEnabled || empty.IsConfirmed || empty.IsTemporaryAuto);
        Assert.Equal(0, empty.PendingConsumableSlotMask);
        Assert.Equal(0, empty.PendingSkillSlotMask);
        Assert.False(empty.IsSoulImprintQueued || empty.HasParticipatedInRun);
        Assert.Null(empty.LastParticipatedMonsterId);
        var moved = await persisted.RoomSlots.SingleAsync(slot => slot.RoomId == room.Id && slot.SlotIndex == 5);
        Assert.Equal(test.ActiveCharacter.Id, moved.CharacterId);
        Assert.Equal(test.ActiveCharacter.UserId, moved.UserId);
        Assert.Equal(lastSeen, moved.LastSeenAtUtc);
        Assert.True(moved.IsAutoEnabled && moved.IsConfirmed && moved.IsTemporaryAuto);
        Assert.Equal(2, moved.PendingConsumableSlotMask);
        Assert.Equal(5, moved.PendingSkillSlotMask);
        Assert.True(moved.IsSoulImprintQueued && moved.HasParticipatedInRun);
        Assert.Equal(room.MonsterId, moved.LastParticipatedMonsterId);
        Assert.Equal(17, (await persisted.Characters.FindAsync(test.ActiveCharacter.Id))!.Hp);
        var currentActivity = await persisted.CharacterActivities.SingleAsync();
        Assert.Equal((activity.CharacterId, activity.Kind, activity.SourceId, activity.StartedAtUtc, activity.EndsAtUtc),
            (currentActivity.CharacterId, currentActivity.Kind, currentActivity.SourceId, currentActivity.StartedAtUtc, currentActivity.EndsAtUtc));
        Assert.Equal(version + 1, (await persisted.Rooms.FindAsync(room.Id))!.Version);
    }

    [Fact]
    public async Task AssignSlotAsync_SwapsOwnedCharactersAndPreservesAccountAutoAndCharacterCommands()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var second = await test.AddCharacterAsync("Priest");
        Assert.Null((await test.Service.AssignSlotAsync(created!.RoomId,
            new AssignRoomSlotRequest { SlotIndex = 5, CharacterId = second.Id }, test.Token)).Error);
        var source = await test.Db.RoomSlots.SingleAsync(slot => slot.RoomId == created.RoomId && slot.SlotIndex == 1);
        var target = await test.Db.RoomSlots.SingleAsync(slot => slot.RoomId == created.RoomId && slot.SlotIndex == 5);
        source.IsAutoEnabled = true;
        (await test.Db.Rooms.FindAsync(created.RoomId))!.IsOwnerAutoEnabled = true;
        source.PendingSkillSlotMask = 3;
        target.IsAutoEnabled = false;
        target.PendingConsumableSlotMask = 1;
        test.ActiveCharacter.Hp = 27;
        second.Hp = 41;
        var now = DateTime.UtcNow;
        test.Db.CharacterBattleMilestones.AddRange(new[] { test.ActiveCharacter, second }.Select(character =>
            new CharacterBattleMilestone { CharacterId = character.Id, Kind = BattleMilestoneService.DungeonClearKind,
                TargetCode = "slime-field", Count = 1, FirstAtUtc = now, LastAtUtc = now }));
        await test.Db.SaveChangesAsync();

        var (updated, error) = await test.Service.AssignSlotAsync(created.RoomId,
            new AssignRoomSlotRequest { SlotIndex = 1, CharacterId = second.Id }, test.Token);

        Assert.Null(error);
        Assert.Equal(second.Id, updated!.Slots.Single(slot => slot.SlotIndex == 1).CharacterId);
        var main = Assert.Single(updated.Slots, slot => slot.CharacterId == test.ActiveCharacter.Id);
        Assert.Equal((5, test.ActiveCharacter.Id), (main.SlotIndex, main.CharacterId));
        Assert.True(updated.IsCurrentUserAutoEnabled);
        Assert.All(updated.Slots.Where(slot => slot.IsOccupied), slot => Assert.True(slot.IsAutoEnabled));
        await using var persisted = test.CreateDbContext();
        var swapped = await persisted.RoomSlots.Where(slot => slot.RoomId == created.RoomId && slot.CharacterId != null).ToListAsync();
        Assert.Equal(2, swapped.Count);
        Assert.Equal(3, swapped.Single(slot => slot.CharacterId == test.ActiveCharacter.Id).PendingSkillSlotMask);
        Assert.Equal(1, swapped.Single(slot => slot.CharacterId == second.Id).PendingConsumableSlotMask);
        Assert.Equal(27, (await persisted.Characters.FindAsync(test.ActiveCharacter.Id))!.Hp);
        Assert.Equal(41, (await persisted.Characters.FindAsync(second.Id))!.Hp);
        Assert.Equal(2, await persisted.CharacterActivities.CountAsync());

        // Swapping back exercises both directions of the unique character index.
        Assert.Null((await test.Service.AssignSlotAsync(created.RoomId,
            new AssignRoomSlotRequest { SlotIndex = 1, CharacterId = test.ActiveCharacter.Id }, test.Token)).Error);
        var restored = await test.Service.GetRoomDetailAsync(created.RoomId, test.Token);
        Assert.Equal(1, restored!.Slots.Single(slot => slot.CharacterId == test.ActiveCharacter.Id).SlotIndex);
    }

    [Fact]
    public async Task AssignSlotAsync_CurrentPositionIsANoOpWithoutHealingOrClearingCommands()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var room = (await test.Db.Rooms.FindAsync(created!.RoomId))!;
        var slot = await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == test.ActiveCharacter.Id);
        test.ActiveCharacter.Hp = 23;
        slot.PendingSkillSlotMask = 7;
        await test.Db.SaveChangesAsync();
        var version = room.Version;

        var (updated, error) = await test.Service.AssignSlotAsync(room.Id,
            new AssignRoomSlotRequest { SlotIndex = 1, CharacterId = test.ActiveCharacter.Id }, test.Token);

        Assert.Null(error);
        Assert.Equal(23, updated!.Slots.Single(slot => slot.IsOccupied).CharacterHp);
        Assert.Equal(7, slot.PendingSkillSlotMask);
        Assert.Equal(version, room.Version);
    }

    [Fact]
    public async Task AssignSlotAsync_CannotMoveIntoAnotherPlayersPosition()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync(null, "Slime", test.Token, isPublic: true);
        await test.AddOtherActiveCharacterAsync();
        Assert.Null((await test.Service.JoinRoomAsync(created!.RoomId,
            new JoinRoomRequest { SlotIndex = 5 }, "other-token")).Error);

        var (updated, error) = await test.Service.AssignSlotAsync(created.RoomId,
            new AssignRoomSlotRequest { SlotIndex = 5, CharacterId = test.ActiveCharacter.Id }, test.Token);

        Assert.Null(updated);
        Assert.Equal("NotCharacterOwner", error);
        Assert.Equal(test.ActiveCharacter.Id, (await test.Db.RoomSlots.SingleAsync(slot => slot.RoomId == created.RoomId && slot.SlotIndex == 1)).CharacterId);
        Assert.Equal(2, (await test.Db.RoomSlots.SingleAsync(slot => slot.RoomId == created.RoomId && slot.SlotIndex == 5)).CharacterId);
        Assert.Equal(2, await test.Db.CharacterActivities.CountAsync());
    }

    [Theory]
    [InlineData(RoomStatus.Preparing, 0)]
    [InlineData(RoomStatus.Cooldown, 1)]
    [InlineData(RoomStatus.WaveTransition, 1)]
    [InlineData(RoomStatus.NotStarted, 1)]
    public async Task AssignSlotAsync_CannotMoveCharactersDuringAnActiveBattle(RoomStatus status, int roundNumber)
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var room = (await test.Db.Rooms.FindAsync(created!.RoomId))!;
        room.Status = status;
        room.RoundNumber = roundNumber;
        await test.Db.SaveChangesAsync();

        var (updated, error) = await test.Service.AssignSlotAsync(room.Id,
            new AssignRoomSlotRequest { SlotIndex = 5, CharacterId = test.ActiveCharacter.Id }, test.Token);

        Assert.Null(updated);
        Assert.Equal("FormationLocked", error);
        var slot = Assert.Single(await test.Db.RoomSlots.Where(slot => slot.RoomId == room.Id && slot.CharacterId != null).ToListAsync());
        Assert.Equal(1, slot.SlotIndex);
        Assert.Equal(test.ActiveCharacter.Id, slot.CharacterId);
    }

    [Fact]
    public async Task AssignSlotAsync_CannotMoveACharacterFromAnotherRoom()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (first, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var second = await test.AddCharacterAsync("Mage");
        (await test.Db.Users.FindAsync(1))!.ActiveCharacterId = second.Id;
        await test.Db.SaveChangesAsync();
        var (other, _) = await test.Service.CreateRoomAsync("Slime", test.Token);

        var (updated, error) = await test.Service.AssignSlotAsync(first!.RoomId,
            new AssignRoomSlotRequest { SlotIndex = 5, CharacterId = second.Id }, test.Token);

        Assert.Null(updated);
        Assert.Equal("CharacterAlreadyInRoom", error);
        Assert.Equal(other!.RoomId, (await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == second.Id)).RoomId);
        Assert.Null((await test.Db.RoomSlots.SingleAsync(slot => slot.RoomId == first.RoomId && slot.SlotIndex == 5)).CharacterId);
    }

    [Fact]
    public async Task AssignSlotAsync_StaleMoveReturnsConflictWithoutClearingOrDuplicatingCharacters()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        await using var staleDb = test.CreateDbContext();
        await staleDb.Rooms.SingleAsync(room => room.Id == created!.RoomId);
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        var staleService = new RoomService(staleDb, new UserService(staleDb, progression, skills), progression,
            ConsumableTestFactory.Create(), skills, RewardTestFactory.CreateService(staleDb, progression));
        Assert.Null((await test.Service.AssignSlotAsync(created!.RoomId,
            new AssignRoomSlotRequest { SlotIndex = 5, CharacterId = test.ActiveCharacter.Id }, test.Token)).Error);

        var (updated, error) = await staleService.AssignSlotAsync(created.RoomId,
            new AssignRoomSlotRequest { SlotIndex = 3, CharacterId = test.ActiveCharacter.Id }, test.Token);

        Assert.Null(updated);
        Assert.Equal("ConcurrencyConflict", error);
        await using var persisted = test.CreateDbContext();
        var slot = Assert.Single(await persisted.RoomSlots.Where(slot => slot.CharacterId != null).ToListAsync());
        Assert.Equal((5, test.ActiveCharacter.Id), (slot.SlotIndex, slot.CharacterId));
        Assert.Single(await persisted.CharacterActivities.ToListAsync());
        var refreshed = await staleService.GetRoomDetailAsync(created.RoomId, test.Token);
        Assert.Equal(5, refreshed!.Slots.Single(slot => slot.IsOccupied).SlotIndex);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemoveSlotAsync_CanRemoveTheOnlyOwnedCharacterAndKeepsAccountAutoWhenAddingAnother(bool autoEnabled)
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (created, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        var room = (await test.Db.Rooms.FindAsync(created!.RoomId))!;
        room.IsOwnerAutoEnabled = autoEnabled;
        test.ActiveCharacter.Hp = 17;
        await test.Db.SaveChangesAsync();

        var (empty, removeError) = await test.Service.RemoveSlotAsync(room.Id, 1, test.Token);

        Assert.Null(removeError);
        Assert.All(empty!.Slots, slot => Assert.False(slot.IsOccupied));
        Assert.Equal(autoEnabled, empty.IsCurrentUserAutoEnabled);
        Assert.Empty(await test.Db.CharacterActivities.ToListAsync());
        Assert.Equal(100, test.ActiveCharacter.Hp);
        // The account still owns the room even when none of its characters is currently in it.
        Assert.Null((await test.Service.SetRoomVisibilityAsync(room.Id, true, test.Token)).Error);
        var next = await test.AddCharacterAsync("Next character");
        var (updated, assignError) = await test.Service.AssignSlotAsync(room.Id,
            new AssignRoomSlotRequest { SlotIndex = 5, CharacterId = next.Id }, test.Token);

        Assert.Null(assignError);
        Assert.Equal(autoEnabled, updated!.IsCurrentUserAutoEnabled);
        var slot = Assert.Single(updated.Slots, slot => slot.IsOccupied);
        Assert.Equal(next.Id, slot.CharacterId);
        Assert.False(slot.IsAutoEnabled); // This character has not cleared this dungeon yet.
        Assert.Equal(autoEnabled, (await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == next.Id)).IsAutoEnabled);
        Assert.Single(await test.Db.CharacterActivities.ToListAsync());
    }

    [Fact]
    public async Task AccountAutoIsIndependentBetweenRooms()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var (first, _) = await test.Service.CreateRoomAsync("Slime", test.Token);
        (await test.Db.Rooms.FindAsync(first!.RoomId))!.IsOwnerAutoEnabled = true;
        var next = await test.AddCharacterAsync("Next character");
        var progression = ProgressionTestFactory.Create();
        var users = new UserService(test.Db, progression, SkillTestFactory.Create());
        Assert.Null((await users.SelectCurrentCharacterAsync(test.Token, next.Id)).Error);
        var (second, error) = await test.Service.CreateRoomAsync("Slime", test.Token);

        Assert.Null(error);
        Assert.Equal(next.Id, Assert.Single(second!.Slots, slot => slot.IsOccupied).CharacterId);
        Assert.False(second.IsCurrentUserAutoEnabled);
        Assert.True((await test.Service.GetRoomDetailAsync(first.RoomId, test.Token))!.IsCurrentUserAutoEnabled);
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
