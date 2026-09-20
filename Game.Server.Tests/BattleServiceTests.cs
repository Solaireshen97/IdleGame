using Game.Server.Data;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public class BattleServiceTests
{
    [Fact]
    public async Task TalentsAffectBothAttackAndDefenseDuringBattle()
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Character.AttackTalentRank = 2;
        test.Character.DefenseTalentRank = 1;
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(33, result!.MonsterHp); // (20 + 2) - 5
        Assert.Equal(94, result.CharacterHp); // 12 - (5 + 1)
        Assert.Equal((20, 5), (test.Character.Attack, test.Character.Defense));
    }

    [Fact]
    public async Task RepeatBattleRestoresHealthToTalentAdjustedMaximum()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 35, characterAttack: 100);
        test.Character.HealthTalentRank = 2;
        test.Room.IsRepeatBattle = true;
        await test.Db.SaveChangesAsync();
        await test.Service.StartPreparationAsync(1, test.Token);
        test.Room.BattleEndedAtUtc = DateTime.UtcNow.AddSeconds(-31);
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.SyncRoomAsync(1);

        Assert.Null(error);
        Assert.Equal(110, result!.CharacterHp);
        Assert.Equal(110, result.CharacterMaxHp);
        Assert.Equal(100, test.Character.MaxHp);
        var progression = ProgressionTestFactory.Create();
        var userService = new UserService(test.Db, progression);
        var (roster, _) = await userService.GetCurrentCharactersAsync(test.Token);
        var room = await new RoomService(test.Db, userService, progression).GetRoomDetailAsync(1, test.Token);
        Assert.Equal(110, Assert.Single(roster!).MaxHp);
        Assert.Equal(110, room!.Slots.Single(slot => slot.SlotIndex == 1).CharacterMaxHp);
    }

    [Fact]
    public async Task SyncRoomAsync_RepeatsVictoryAfterThirtySecondsAndRestoresPartyHp()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 35, characterAttack: 100);
        test.Room.IsRepeatBattle = true;
        await test.Db.SaveChangesAsync();

        var (victory, _) = await test.Service.StartPreparationAsync(1, test.Token);
        var (waiting, _) = await test.Service.SyncRoomAsync(1);

        Assert.Equal(RoomStatus.BattleOver, victory!.RoomStatus);
        Assert.Equal(RoomStatus.BattleOver, waiting!.RoomStatus);
        Assert.Equal(0, test.Monster.Hp);
        Assert.Equal(35, test.Character.Hp);

        test.Room.BattleEndedAtUtc = DateTime.UtcNow.AddSeconds(-31);
        await test.Db.SaveChangesAsync();
        var (restarted, error) = await test.Service.SyncRoomAsync(1);

        Assert.Null(error);
        Assert.Equal(RoomStatus.NotStarted, restarted!.RoomStatus);
        Assert.Equal(test.Monster.MaxHp, test.Monster.Hp);
        Assert.Equal(test.Character.MaxHp, test.Character.Hp);
        Assert.Null(test.Room.BattleEndedAtUtc);
    }

    [Fact]
    public async Task SyncRoomAsync_RepeatBattleStopsAfterDefeat()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 1, characterAttack: 1, monsterAttack: 100);
        test.Room.IsRepeatBattle = true;
        await test.Db.SaveChangesAsync();
        await test.Service.StartPreparationAsync(1, test.Token);
        test.Room.BattleEndedAtUtc = DateTime.UtcNow.AddSeconds(-31);
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.SyncRoomAsync(1);

        Assert.Null(error);
        Assert.Equal(RoomStatus.BattleOver, result!.RoomStatus);
        Assert.Equal(0, test.Character.Hp);
        Assert.True(test.Monster.Hp > 0);
        Assert.Equal(0, test.Character.Experience);
    }

    [Fact]
    public async Task SyncRoomAsync_RepeatBattleImmediatelyUsesUnlockedAutoAfterRestart()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 40, characterAttack: 100);
        test.Room.IsRepeatBattle = true;
        var mainSlot = await test.Db.RoomSlots.SingleAsync(slot => slot.RoomId == 1 && slot.IsMainControl);
        mainSlot.IsAutoEnabled = true;
        await test.Db.SaveChangesAsync();
        await test.Service.StartPreparationAsync(1, test.Token);
        test.Room.BattleEndedAtUtc = DateTime.UtcNow.AddSeconds(-31);
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.SyncRoomAsync(1);

        Assert.Null(error);
        Assert.Equal(RoomStatus.BattleOver, result!.RoomStatus);
        Assert.Equal(0, test.Monster.Hp);
        Assert.Equal(test.Character.MaxHp, test.Character.Hp);
        Assert.Contains(result.Logs, log => log.Contains("next dungeon battle"));
        Assert.True(test.Room.BattleEndedAtUtc > DateTime.UtcNow.AddSeconds(-5));
        Assert.Equal(2, test.Character.Level);
        Assert.Equal(1, test.Character.TalentPoints);
        Assert.Equal(0, test.Character.Experience);
    }

    [Fact]
    public async Task SyncRoomAsync_SingleBattleDoesNotRestartAfterVictory()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        await test.Service.StartPreparationAsync(1, test.Token);
        test.Room.BattleEndedAtUtc = DateTime.UtcNow.AddSeconds(-31);
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.SyncRoomAsync(1);

        Assert.Null(error);
        Assert.Equal(RoomStatus.BattleOver, result!.RoomStatus);
        Assert.Equal(0, test.Monster.Hp);
    }

    [Fact]
    public async Task SetSlotAutoAsync_WithoutDungeonClear_IsRejected()
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Db.UserDungeonClears.RemoveRange(await test.Db.UserDungeonClears.ToListAsync());
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.SetSlotAutoAsync(1, new Game.Shared.Dtos.SetSlotAutoRequest { SlotIndex = 1, IsAutoEnabled = true }, test.Token);

        Assert.Null(result);
        Assert.Equal("AutoNotUnlocked", error);
    }

    [Fact]
    public async Task ExecuteRoundAsync_VictoryRecordsDungeonClearOnlyOnce()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        test.Db.UserDungeonClears.RemoveRange(await test.Db.UserDungeonClears.ToListAsync());
        await test.Db.SaveChangesAsync();

        var (_, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        var clear = Assert.Single(await test.Db.UserDungeonClears.Where(item => item.UserId == 1 && item.DungeonId == 1).ToListAsync());
        Assert.NotEqual(default, clear.ClearedAtUtc);
    }

    [Fact]
    public async Task ExecuteRoundAsync_VictoryRewardsEachPartyCharacterOnceEvenWhenLaterSlotDoesNotAttack()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        var second = await test.AddSlotAsync(2, "Mage", attack: 10);

        var (victory, error) = await test.Service.StartPreparationAsync(1, test.Token);
        await test.Service.SyncRoomAsync(1);

        Assert.Null(error);
        Assert.Equal(RoomStatus.BattleOver, victory!.RoomStatus);
        Assert.Equal(10, test.Character.Experience);
        Assert.Equal(10, second.Experience);
        Assert.Equal(1, test.Character.Level);
        Assert.Equal(0, test.Character.TalentPoints);
        Assert.Equal(100, test.Character.MaxHp);
        Assert.Equal(100, test.Character.Attack);
        Assert.Contains(victory.Logs, log => log.Contains("Mage gains 10 EXP"));
        var progression = ProgressionTestFactory.Create();
        var detail = await new RoomService(test.Db, new UserService(test.Db, progression), progression).GetRoomDetailAsync(1, test.Token);
        var mainSlot = detail!.Slots.Single(slot => slot.SlotIndex == 1);
        Assert.Equal(1, mainSlot.CharacterLevel);
        Assert.Equal(10, mainSlot.CharacterExperience);
        Assert.Equal(20, mainSlot.ExperienceToNextLevel);
        Assert.Equal(0, mainSlot.TalentPoints);
    }

    [Fact]
    public async Task ExecuteRoundAsync_FirstRound_AppliesBothAttacks()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var (result, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(93, result!.CharacterHp);
        Assert.Equal(35, result.MonsterHp);
        Assert.Equal(RoomStatus.Cooldown, result.RoomStatus);
    }

    [Fact]
    public async Task ExecuteRoundAsync_WithoutPreparation_IsRejectedWithoutChangingHp()
    {
        await using var test = await BattleTestContext.CreateAsync();

        var (result, error) = await test.Service.ExecuteRoundAsync(1, test.Token);

        Assert.Equal("PreparationRequired", error);
        Assert.Equal(100, result!.CharacterHp);
        Assert.Equal(50, result.MonsterHp);
    }

    [Fact]
    public async Task StartPreparationAsync_ConfirmsPartyAndClearsConfirmationsAfterRound()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1, characterDefense: 99);
        await test.AddSlotAsync(2, "Mage", attack: 10);

        var (result, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(RoomStatus.Cooldown, result!.RoomStatus);
        Assert.All(await test.Db.RoomSlots.Where(slot => slot.RoomId == 1).ToListAsync(), slot => Assert.False(slot.IsConfirmed));
    }

    [Fact]
    public async Task StartPreparationAsync_MultipleMembers_OnlyLastPreparationExecutesRound()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1, characterDefense: 99);
        await test.AddOtherMemberAsync();

        var (first, firstError) = await test.Service.StartPreparationAsync(1, test.Token);
        var (last, lastError) = await test.Service.StartPreparationAsync(1, "other-token");

        Assert.Null(firstError);
        Assert.Equal(RoomStatus.Preparing, first!.RoomStatus);
        Assert.Null(lastError);
        Assert.Equal(RoomStatus.Cooldown, last!.RoomStatus);
        Assert.Equal(30, last.MonsterHp);
    }

    [Fact]
    public async Task SyncAsync_PreparationTimeout_UsesTemporaryAutoAndClearsItAfterRound()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1, characterDefense: 99);
        await test.AddOtherMemberAsync();
        await test.Service.StartPreparationAsync(1, test.Token);
        test.Room.PreparationStartedAtUtc = DateTime.UtcNow.AddSeconds(-31);
        test.Room.Version++;
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.SyncAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(RoomStatus.Cooldown, result!.RoomStatus);
        Assert.Contains(result.Logs, log => log.Contains("temporarily set to Auto"));
        Assert.All(await test.Db.RoomSlots.Where(slot => slot.RoomId == 1).ToListAsync(), slot => Assert.False(slot.IsTemporaryAuto));
    }

    [Fact]
    public async Task SyncAsync_PreparationTimeoutDisabled_MixedTeamKeepsWaiting()
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Room.IsPreparationTimeoutEnabled = false;
        await test.AddOtherMemberAsync();
        await test.Service.StartPreparationAsync(1, test.Token);
        test.Room.PreparationStartedAtUtc = DateTime.UtcNow.AddSeconds(-31);
        test.Room.Version++;
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.SyncAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(RoomStatus.Preparing, result!.RoomStatus);
        Assert.Empty(result.Logs);
        Assert.All(await test.Db.RoomSlots.Where(slot => slot.RoomId == 1).ToListAsync(), slot => Assert.False(slot.IsTemporaryAuto));
    }

    [Fact]
    public async Task SyncAsync_AllMembersAuto_StartsRoundAndUsesThirtySecondCooldown()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1, characterDefense: 99);
        var otherSlot = await test.AddOtherMemberAsync();
        (await test.Db.RoomSlots.SingleAsync(slot => slot.RoomId == 1 && slot.SlotIndex == 1)).IsAutoEnabled = true;
        otherSlot.IsAutoEnabled = true;
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.SyncAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(RoomStatus.Cooldown, result!.RoomStatus);
        Assert.InRange((result.NextRoundAvailableAtUtc!.Value - result.ServerTimeUtc).TotalSeconds, 29, 31);
    }

    [Fact]
    public async Task SyncAsync_AutoDisabled_DoesNotAdvanceExpiredCooldown()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var slot = await test.Db.RoomSlots.SingleAsync(x => x.RoomId == 1 && x.SlotIndex == 1);
        slot.IsAutoEnabled = true;
        await test.Db.SaveChangesAsync();
        await test.Service.SyncAsync(1, test.Token);
        slot.IsAutoEnabled = false;
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        test.Room.Version++;
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.SyncAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(RoomStatus.NotStarted, result!.RoomStatus);
        Assert.Equal(35, result.MonsterHp);
    }

    [Fact]
    public async Task SetSlotAutoAsync_DisablingDuringAutoCooldownOpensManualControlsWithoutAnotherRound()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var slot = await test.Db.RoomSlots.SingleAsync(x => x.RoomId == 1 && x.SlotIndex == 1);
        slot.IsAutoEnabled = true;
        await test.Db.SaveChangesAsync();
        await test.Service.SyncAsync(1, test.Token);
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(9);
        test.Room.RoundCooldownDurationSeconds = null; // Rooms already cooling down before the migration.
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.SetSlotAutoAsync(1, new Game.Shared.Dtos.SetSlotAutoRequest { SlotIndex = 1, IsAutoEnabled = false }, test.Token);
        var (synced, syncError) = await test.Service.SyncAsync(1, test.Token);

        Assert.Null(error);
        Assert.Null(syncError);
        Assert.Equal(RoomStatus.NotStarted, result!.RoomStatus);
        Assert.Equal(RoomStatus.NotStarted, synced!.RoomStatus);
        Assert.Equal(35, test.Monster.Hp);
        Assert.Equal(93, test.Character.Hp);
        Assert.False(slot.IsAutoEnabled);
        Assert.InRange((test.Room.PreparationStartedAtUtc!.Value - DateTime.UtcNow).TotalSeconds, -5, 0);
    }

    [Fact]
    public async Task SetSlotAutoAsync_DisablingEarlyChangesAutoCooldownToTenSeconds()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var slot = await test.Db.RoomSlots.SingleAsync(x => x.RoomId == 1 && x.SlotIndex == 1);
        slot.IsAutoEnabled = true;
        await test.Db.SaveChangesAsync();
        await test.Service.SyncAsync(1, test.Token);

        var (result, error) = await test.Service.SetSlotAutoAsync(1, new Game.Shared.Dtos.SetSlotAutoRequest { SlotIndex = 1, IsAutoEnabled = false }, test.Token);

        Assert.Null(error);
        Assert.Equal(RoomStatus.Cooldown, result!.RoomStatus);
        Assert.Equal(BattleRules.RoundCooldownSeconds, test.Room.RoundCooldownDurationSeconds);
        Assert.InRange((result.NextRoundAvailableAtUtc!.Value - result.ServerTimeUtc).TotalSeconds, 9, 11);
        Assert.Equal(35, test.Monster.Hp);
    }

    [Fact]
    public async Task SyncAsync_UnpreparedRoomTimesOutAndRunsOneRound()
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Room.PreparationStartedAtUtc = DateTime.UtcNow.AddSeconds(-31);
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.SyncAsync(1, test.Token);
        var (again, nextError) = await test.Service.SyncAsync(1, test.Token);

        Assert.Null(error);
        Assert.Null(nextError);
        Assert.Equal(RoomStatus.Cooldown, result!.RoomStatus);
        Assert.Equal(RoomStatus.Cooldown, again!.RoomStatus);
        Assert.Equal(35, test.Monster.Hp);
        Assert.Equal(93, test.Character.Hp);
        Assert.False((await test.Db.RoomSlots.SingleAsync(x => x.RoomId == 1 && x.SlotIndex == 1)).IsAutoEnabled);
    }

    [Fact]
    public async Task SyncAsync_UnpreparedRoomWithTimeoutDisabledWaitsForManualAction()
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Room.IsPreparationTimeoutEnabled = false;
        test.Room.PreparationStartedAtUtc = DateTime.UtcNow.AddSeconds(-31);
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.SyncAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(RoomStatus.NotStarted, result!.RoomStatus);
        Assert.Equal(50, test.Monster.Hp);
    }

    [Fact]
    public async Task ExecuteRoundAsync_DamageBelowDefense_DealsAtLeastOne()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterDefense: 99, monsterAttack: 1, characterDefense: 99);
        var (result, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(49, result!.MonsterHp);
        Assert.Equal(99, result.CharacterHp);
    }

    [Fact]
    public async Task ExecuteRoundAsync_PlayerKillsMonster_DoesNotCounterattack()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        var (result, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.True(result!.IsVictory);
        Assert.Equal(100, result.CharacterHp);
        Assert.Equal(RoomStatus.BattleOver, result.RoomStatus);
        Assert.NotNull(result.BattleEndedAtUtc);
    }

    [Fact]
    public async Task ExecuteRoundAsync_MonsterKillsPlayer_SetsBattleOver()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 5, monsterAttack: 100);
        var (result, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.True(result!.IsCharacterDead);
        Assert.Equal(0, result.CharacterHp);
        Assert.Equal(RoomStatus.BattleOver, result.RoomStatus);
    }

    [Fact]
    public async Task ExecuteRoundAsync_DuringCooldown_DoesNotChangeHp()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var (firstResult, _) = await test.Service.StartPreparationAsync(1, test.Token);
        var (result, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Equal("RoundCooldown", error);
        Assert.Equal(firstResult!.CharacterHp, result!.CharacterHp);
        Assert.Equal(firstResult.MonsterHp, result.MonsterHp);
    }

    [Fact]
    public async Task ExecuteRoundAsync_AfterCooldown_CanExecuteAgain()
    {
        await using var test = await BattleTestContext.CreateAsync();
        await test.Service.StartPreparationAsync(1, test.Token);
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        test.Room.Version++;
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.StartPreparationAsync(1, test.Token);
        Assert.Null(error);
        Assert.Equal(20, result!.MonsterHp);
    }

    [Fact]
    public async Task ExecuteRoundAsync_AfterBattleOver_IsRejectedWithoutChanges()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        var (firstResult, _) = await test.Service.StartPreparationAsync(1, test.Token);
        var (result, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Equal("BattleOver", error);
        Assert.Equal(firstResult!.MonsterHp, result!.MonsterHp);
    }

    [Fact]
    public async Task ResetBattleAsync_AfterVictory_RestoresMonsterPartyAndClearsRoundState()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 35, characterAttack: 100);
        var second = await test.AddSlotAsync(2, "Mage", hp: 42);
        await test.Service.StartPreparationAsync(1, test.Token);

        var (success, error) = await test.Service.ResetBattleAsync(1, test.Token);
        Assert.True(success);
        Assert.Null(error);
        Assert.Equal(50, test.Monster.Hp);
        Assert.Equal(100, test.Character.Hp);
        Assert.Equal(100, second.Hp);
        Assert.Equal(RoomStatus.NotStarted, test.Room.Status);
        Assert.Null(test.Room.NextRoundAvailableAtUtc);
        Assert.Null(test.Room.BattleEndedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetBattleAsync_AfterDefeat_RestoresPartyForRetry(bool isRepeatBattle)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 5, characterAttack: 1, monsterAttack: 100);
        test.Room.IsRepeatBattle = isRepeatBattle;
        await test.Db.SaveChangesAsync();
        await test.Service.StartPreparationAsync(1, test.Token);
        Assert.Equal(RoomStatus.BattleOver, test.Room.Status);
        Assert.Equal(0, test.Character.Hp);

        var (success, error) = await test.Service.ResetBattleAsync(1, test.Token);
        Assert.True(success);
        Assert.Null(error);
        Assert.Equal(100, test.Character.Hp);
        Assert.Equal(50, test.Monster.Hp);
        Assert.Equal(RoomStatus.NotStarted, test.Room.Status);
    }

    [Theory]
    [InlineData(RoomStatus.NotStarted)]
    [InlineData(RoomStatus.Preparing)]
    [InlineData(RoomStatus.Cooldown)]
    public async Task ResetBattleAsync_BeforeBattleOver_IsRejectedWithoutChanges(RoomStatus status)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 75);
        test.Room.Status = status;
        test.Monster.Hp = 30;
        await test.Db.SaveChangesAsync();

        var (success, error) = await test.Service.ResetBattleAsync(1, test.Token);

        Assert.False(success);
        Assert.Equal("BattleNotOver", error);
        Assert.Equal(status, test.Room.Status);
        Assert.Equal(30, test.Monster.Hp);
        Assert.Equal(75, test.Character.Hp);
    }

    [Fact]
    public async Task ResetBattleAsync_NonOwner_IsRejectedWithoutChanges()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 75, characterAttack: 100);
        await test.AddOtherMemberAsync();
        await test.Service.StartPreparationAsync(1, test.Token);
        await test.Service.StartPreparationAsync(1, "other-token");
        Assert.Equal(RoomStatus.BattleOver, test.Room.Status);

        var (success, error) = await test.Service.ResetBattleAsync(1, "other-token");

        Assert.False(success);
        Assert.Equal("NotOwner", error);
        Assert.Equal(RoomStatus.BattleOver, test.Room.Status);
        Assert.Equal(0, test.Monster.Hp);
        Assert.Equal(75, test.Character.Hp);
    }

    [Fact]
    public async Task ResetBattleAsync_DuringRepeatVictoryCountdown_IsRejected()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 75, characterAttack: 100);
        test.Room.IsRepeatBattle = true;
        await test.Db.SaveChangesAsync();
        await test.Service.StartPreparationAsync(1, test.Token);

        var (success, error) = await test.Service.ResetBattleAsync(1, test.Token);

        Assert.False(success);
        Assert.Equal("RepeatBattlePending", error);
        Assert.Equal(RoomStatus.BattleOver, test.Room.Status);
        Assert.Equal(0, test.Monster.Hp);
        Assert.Equal(75, test.Character.Hp);
    }

    [Fact]
    public async Task ExecuteRoundAsync_NonMember_IsRejectedWithoutChanges()
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Db.Users.Add(new User { Id = 2, UserName = "other", PasswordHash = "x", ActiveCharacterId = 2 });
        test.Db.Characters.Add(new Character { Id = 2, UserId = 2, Name = "Other", Hp = 100, MaxHp = 100, Attack = 20, Defense = 5 });
        test.Db.UserLoginSessions.Add(new UserLoginSession { UserId = 2, Token = "other-token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.StartPreparationAsync(1, "other-token");
        Assert.Null(result);
        Assert.Equal("NotInRoom", error);
        Assert.Equal(50, test.Monster.Hp);
        Assert.Equal(100, test.Character.Hp);
    }

    [Fact]
    public async Task ExecuteRoundAsync_ConcurrentRequests_OnlyOneSucceeds()
    {
        await using var test = await BattleTestContext.CreateAsync();
        await using var firstDb = test.CreateDbContext();
        await using var secondDb = test.CreateDbContext();
        var progression = ProgressionTestFactory.Create();
        var firstService = new BattleService(firstDb, new UserService(firstDb, progression), progression);
        var secondService = new BattleService(secondDb, new UserService(secondDb, progression), progression);

        var results = await Task.WhenAll(firstService.StartPreparationAsync(1, test.Token), secondService.StartPreparationAsync(1, test.Token));
        Assert.Single(results.Where(result => result.Error is null));
    }

    [Fact]
    public async Task ExecuteRoundAsync_UsesSlotOrderForPartyAttacks()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1, characterDefense: 99);
        await test.AddSlotAsync(2, "Mage", attack: 10);

        var (result, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.StartsWith("Slot 1 Knight attacks", result!.Logs[0]);
        Assert.StartsWith("Slot 2 Mage attacks", result.Logs[1]);
    }

    [Fact]
    public async Task ExecuteRoundAsync_StopsLaterSlotsWhenMonsterDies()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        await test.AddSlotAsync(2, "Mage", attack: 100);

        var (result, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Single(result!.Logs.Where(x => x.Contains("attacks Slime")));
        Assert.DoesNotContain(result.Logs, x => x.Contains("Slot 2 Mage attacks"));
        Assert.DoesNotContain(result.Logs, x => x.Contains("Slime attacks"));
    }

    [Fact]
    public async Task ExecuteRoundAsync_TargetsNextLivingLowestSlot()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 5, monsterAttack: 100);
        var second = await test.AddSlotAsync(2, "Mage", defense: 5);

        await test.Service.StartPreparationAsync(1, test.Token);
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        test.Room.Version++;
        await test.Db.SaveChangesAsync();
        var (result, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(0, test.Character.Hp);
        Assert.Equal(5, second.Hp);
        Assert.Contains(result!.Logs, x => x.Contains("attacks Slot 2 Mage"));
    }

    [Fact]
    public async Task SetSlotAutoAsync_DuringPreparation_WhenItConfirmsLastMember_ExecutesRound()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1, characterDefense: 99);
        var other = new Character { Id = 2, UserId = 2, Name = "Mage", Hp = 100, MaxHp = 100, Attack = 10, Defense = 99 };
        test.Db.AddRange(
            new User { Id = 2, UserName = "other", PasswordHash = "x", ActiveCharacterId = 2 },
            other,
            new RoomSlot { RoomId = 1, SlotIndex = 2, UserId = 2, CharacterId = 2 },
            new UserDungeonClear { UserId = 2, DungeonId = 1, ClearedAtUtc = DateTime.UtcNow },
            new UserLoginSession { UserId = 2, Token = "other-token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
        await test.Db.SaveChangesAsync();

        var (preparation, preparationError) = await test.Service.StartPreparationAsync(1, test.Token);
        var (result, error) = await test.Service.SetSlotAutoAsync(1, new Game.Shared.Dtos.SetSlotAutoRequest { SlotIndex = 2, IsAutoEnabled = true }, "other-token");

        Assert.Null(preparationError);
        Assert.Equal(RoomStatus.Preparing, preparation!.RoomStatus);
        Assert.Null(error);
        Assert.Equal(RoomStatus.Cooldown, result!.RoomStatus);
        Assert.Equal(30, result.MonsterHp);
        Assert.All(await test.Db.RoomSlots.Where(slot => slot.RoomId == 1).ToListAsync(), slot => Assert.False(slot.IsConfirmed));
    }

    private sealed class BattleTestContext : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly DbContextOptions<GameDbContext> _options;

        private BattleTestContext(string databasePath, DbContextOptions<GameDbContext> options, GameDbContext db, Room room, Character character, Monster monster)
        {
            _databasePath = databasePath;
            _options = options;
            Db = db;
            Room = room;
            Character = character;
            Monster = monster;
            var progression = ProgressionTestFactory.Create();
            Service = new BattleService(db, new UserService(db, progression), progression);
        }

        public string Token => "token";
        public GameDbContext Db { get; }
        public Room Room { get; }
        public Character Character { get; }
        public Monster Monster { get; }
        public BattleService Service { get; }

        public async Task<Character> AddSlotAsync(int slotIndex, string name, int hp = 100, int attack = 20, int defense = 5)
        {
            var character = new Character { UserId = 1, Name = name, Hp = hp, MaxHp = 100, Attack = attack, Defense = defense };
            Db.Characters.Add(character);
            await Db.SaveChangesAsync();
            Db.RoomSlots.Add(new RoomSlot { RoomId = Room.Id, SlotIndex = slotIndex, CharacterId = character.Id, UserId = 1 });
            await Db.SaveChangesAsync();
            return character;
        }

        public async Task<RoomSlot> AddOtherMemberAsync()
        {
            var character = new Character { Id = 2, UserId = 2, Name = "Mage", Hp = 100, MaxHp = 100, Attack = 10, Defense = 99 };
            var slot = new RoomSlot { RoomId = Room.Id, SlotIndex = 2, CharacterId = 2, UserId = 2 };
            Db.AddRange(new User { Id = 2, UserName = "other", PasswordHash = "x", ActiveCharacterId = 2 }, character, slot, new UserLoginSession { UserId = 2, Token = "other-token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            Db.UserDungeonClears.Add(new UserDungeonClear { UserId = 2, DungeonId = 1, ClearedAtUtc = DateTime.UtcNow });
            await Db.SaveChangesAsync();
            return slot;
        }

        public static async Task<BattleTestContext> CreateAsync(int characterHp = 100, int characterAttack = 20, int characterDefense = 5, int monsterAttack = 12, int monsterDefense = 5)
        {
            var path = Path.Combine(Path.GetTempPath(), $"idlegame-tests-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            var db = new GameDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var user = new User { Id = 1, UserName = "user", PasswordHash = "x", ActiveCharacterId = 1 };
            var character = new Character { Id = 1, UserId = 1, Name = "Knight", Hp = characterHp, MaxHp = 100, Attack = characterAttack, Defense = characterDefense };
            var monster = new Monster { Id = 1, Name = "Slime", Hp = 50, MaxHp = 50, Attack = monsterAttack, Defense = monsterDefense };
            var room = new Room { Id = 1, DungeonId = 1, MonsterId = 1, OwnerUserId = 1, SlotCount = 5, Status = RoomStatus.NotStarted };
            db.AddRange(new Dungeon { Id = 1, Code = "slime-field", Name = "史莱姆平原", MonsterName = "Slime", MonsterMaxHp = 50, MonsterAttack = monsterAttack, MonsterDefense = monsterDefense, SlotCount = 5, SortOrder = 1 }, user, character, monster, room, new UserDungeonClear { UserId = 1, DungeonId = 1, ClearedAtUtc = DateTime.UtcNow }, new RoomSlot { Id = 1, RoomId = 1, SlotIndex = 1, UserId = 1, CharacterId = 1, IsMainControl = true }, new UserLoginSession { Id = 1, UserId = 1, Token = "token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();
            return new BattleTestContext(path, options, db, room, character, monster);
        }

        public GameDbContext CreateDbContext() => new(_options);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            File.Delete(_databasePath);
        }
    }
}
