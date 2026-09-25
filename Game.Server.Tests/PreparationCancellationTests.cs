using Game.Server.Data;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Fact]
    public async Task CancelPreparation_EarlyReadyCanBeWithdrawnUntilTheRoundActuallySettles()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token)).Error);
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token, 1)).Error);
        var deadline = test.Room.NextRoundAvailableAtUtc;
        var cooldown = test.Room.RoundCooldownDurationSeconds;
        var monsterHp = test.Monster.Hp;
        var characterHp = test.Character.Hp;

        var (cancelled, error) = await test.Service.CancelPreparationAsync(1, test.Token, 1, 1);

        Assert.Null(error);
        Assert.Equal(RoomStatus.Cooldown, cancelled!.RoomStatus);
        Assert.False((await test.Db.RoomSlots.SingleAsync()).IsConfirmed);
        Assert.Equal(deadline, test.Room.NextRoundAvailableAtUtc);
        Assert.Equal(cooldown, test.Room.RoundCooldownDurationSeconds);
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();

        var (waiting, waitingError) = await test.Service.SyncRoomAsync(1);

        Assert.Null(waitingError);
        Assert.Equal(RoomStatus.NotStarted, waiting!.RoomStatus);
        Assert.Equal(1, test.Room.RoundNumber);
        Assert.Equal(monsterHp, test.Monster.Hp);
        Assert.Equal(characterHp, test.Character.Hp);
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token, 1)).Error);
        Assert.Equal(2, test.Room.RoundNumber);
        Assert.True(test.Monster.Hp < monsterHp);
    }

    [Fact]
    public async Task CancelPreparation_WithdrawsOnlyOwnedManualCharactersAndPreservesOtherConfirmations()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        var guest = await test.AddOtherMemberAsync();
        var manualAlt = await test.AddSlotAsync(3, "Manual alt", attack: 1);
        var autoAlt = await test.AddSlotAsync(4, "Auto alt", attack: 1);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        (await test.Db.RoomSlots.SingleAsync(slot => slot.IsMainControl)).IsAutoEnabled = true;
        test.Db.CharacterBattleMilestones.RemoveRange(await test.Db.CharacterBattleMilestones
            .Where(milestone => milestone.CharacterId == test.Character.Id).ToListAsync());
        test.Db.CharacterBattleMilestones.Add(new CharacterBattleMilestone
        {
            CharacterId = autoAlt.Id, Kind = BattleMilestoneService.DungeonClearKind,
            TargetCode = "slime-field", Count = 1, FirstAtUtc = DateTime.UtcNow, LastAtUtc = DateTime.UtcNow
        });
        await test.Db.SaveChangesAsync();
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token)).Error);
        Assert.Null((await test.Service.StartPreparationAsync(1, "other-token")).Error);
        Assert.Null((await test.Service.StartPreparationAsync(1, "other-token", 1)).Error);
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token, 1)).Error);
        var slots = await test.Db.RoomSlots.OrderBy(slot => slot.SlotIndex).ToListAsync();
        Assert.All(slots, slot => Assert.True(slot.IsConfirmed));
        var autoFlags = slots.Select(slot => (slot.IsAutoEnabled, slot.IsTemporaryAuto)).ToArray();

        Assert.Null((await test.Service.CancelPreparationAsync(1, test.Token, 1, 1)).Error);

        Assert.False(slots.Single(slot => slot.IsMainControl).IsConfirmed);
        Assert.False(slots.Single(slot => slot.CharacterId == manualAlt.Id).IsConfirmed);
        Assert.True(guest.IsConfirmed);
        // The group switch only makes characters with their own clear unlock effectively Auto.
        Assert.True(slots.Single(slot => slot.CharacterId == autoAlt.Id).IsConfirmed);
        Assert.Equal(autoFlags, slots.Select(slot => (slot.IsAutoEnabled, slot.IsTemporaryAuto)).ToArray());
        Assert.Equal(1, test.Room.RoundNumber);
    }

    [Fact]
    public async Task CancelPreparation_PreservesQueuedSkillSoulAndConsumableWithoutSpendingThem()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 60, characterAttack: 1, monsterAttack: 1);
        test.Character.ProfessionCode = "knight";
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        await test.Db.SaveChangesAsync();
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token)).Error);
        await test.CompleteCooldownAndPrepareAsync();
        await test.CompleteCooldownAndPrepareAsync();
        await test.AddPotionAsync(test.Character, 2, autoUse: false);
        await test.AddSkillAsync(test.Character, 1, "knight-strike", autoUse: false);
        test.Db.CharacterSoulImprints.Add(new CharacterSoulImprint
        {
            CharacterId = test.Character.Id, SoulImprintCode = "deep-core",
            EquippedSlotIndex = SoulImprintRules.SlotIndex
        });
        await test.Db.SaveChangesAsync();
        Assert.Null((await test.Service.QueueConsumableAsync(new QueueConsumableRequest
            { RoomId = 1, CharacterId = test.Character.Id, ConsumableSlotIndex = 1 }, test.Token)).Error);
        Assert.Null((await test.Service.QueueSkillAsync(new QueueSkillRequest
            { RoomId = 1, CharacterId = test.Character.Id, SkillSlotIndex = 1, IsQueued = true }, test.Token)).Error);
        Assert.Null((await test.Service.QueueSoulImprintAsync(new QueueSoulImprintRequest
            { RoomId = 1, CharacterId = test.Character.Id, IsQueued = true }, test.Token)).Error);
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token, 3)).Error);

        Assert.Null((await test.Service.CancelPreparationAsync(1, test.Token, 3, 1)).Error);

        var slot = await test.Db.RoomSlots.SingleAsync();
        Assert.False(slot.IsConfirmed);
        Assert.Equal(1, slot.PendingConsumableSlotIndex);
        Assert.Equal(SkillRules.SlotMask(1), slot.PendingSkillSlotMask);
        Assert.True(slot.IsSoulImprintQueued);
        Assert.False(slot.IsAutoEnabled);
        Assert.Equal(2, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Empty(await test.Db.BattleSkillCooldowns.ToListAsync());
        Assert.Empty(await test.Db.BattleConsumableCooldowns.ToListAsync());
    }

    [Theory]
    [InlineData(RoomStatus.Preparing)]
    [InlineData(RoomStatus.Cooldown)]
    [InlineData(RoomStatus.NotStarted)]
    public async Task CancelPreparation_IsIdempotentInEveryUnsettledPreparationPhase(RoomStatus phase)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        await test.AddOtherMemberAsync();
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token)).Error);
        if (phase != RoomStatus.Preparing)
        {
            Assert.Null((await test.Service.StartPreparationAsync(1, "other-token")).Error);
            Assert.Null((await test.Service.StartPreparationAsync(1, test.Token, 1)).Error);
            if (phase == RoomStatus.NotStarted)
            {
                test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
                await test.Db.SaveChangesAsync();
                Assert.Null((await test.Service.SyncRoomAsync(1)).Error);
            }
        }
        Assert.Equal(phase, test.Room.Status);
        var round = test.Room.RoundNumber;
        var started = test.Room.PreparationStartedAtUtc;
        var deadline = test.Room.NextRoundAvailableAtUtc;
        var hp = test.Monster.Hp;

        var first = await test.Service.CancelPreparationAsync(1, test.Token, round, 1);
        var second = await test.Service.CancelPreparationAsync(1, test.Token, round, 1);

        Assert.Null(first.Error);
        Assert.Null(second.Error);
        Assert.NotNull(second.Result);
        Assert.Equal(phase, test.Room.Status);
        Assert.Equal(round, test.Room.RoundNumber);
        Assert.Equal(hp, test.Monster.Hp);
        Assert.Equal(started, test.Room.PreparationStartedAtUtc);
        Assert.Equal(deadline, test.Room.NextRoundAvailableAtUtc);
        Assert.False((await test.Db.RoomSlots.SingleAsync(slot => slot.IsMainControl)).IsConfirmed);
    }

    [Fact]
    public async Task CancelPreparation_DoesNotExtendTheExistingPreparationTimeout()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        await test.AddOtherMemberAsync();
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token)).Error);
        var started = DateTime.UtcNow.AddSeconds(-BattleRules.PreparationTimeoutSeconds - 1);
        test.Room.PreparationStartedAtUtc = started;
        await test.Db.SaveChangesAsync();

        Assert.Null((await test.Service.CancelPreparationAsync(1, test.Token, 0, 1)).Error);
        Assert.Equal(started, test.Room.PreparationStartedAtUtc);
        Assert.Equal(started.AddSeconds(BattleRules.PreparationTimeoutSeconds),
            (await test.GetRoomDetailAsync())!.PreparationExpiresAtUtc);
        var (settled, error) = await test.Service.SyncRoomAsync(1);

        Assert.Null(error);
        Assert.Equal(1, test.Room.RoundNumber);
        Assert.Contains(settled!.Logs, log => log.Contains("准备超时"));
    }

    [Fact]
    public async Task CancelPreparation_StaleRoundCannotWithdrawTheNextRoundConfirmation()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token, 0)).Error);
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token, 1)).Error);

        var (_, error) = await test.Service.CancelPreparationAsync(1, test.Token, 0, 1);

        Assert.Equal("StaleRound", error);
        Assert.Equal(1, test.Room.RoundNumber);
        Assert.True((await test.Db.RoomSlots.SingleAsync()).IsConfirmed);
    }

    [Fact]
    public async Task CancelPreparation_AnOldRoundZeroRequestCannotWithdrawANewRepeatRun()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1);
        test.Room.IsRepeatBattle = true;
        await test.Db.SaveChangesAsync();
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token, 0)).Error);
        Assert.Equal(RoomStatus.BattleOver, test.Room.Status);
        test.Room.BattleEndedAtUtc = DateTime.UtcNow.AddSeconds(-BattleRules.RepeatBattleDelaySeconds - 1);
        await test.Db.SaveChangesAsync();
        Assert.Null((await test.Service.SyncRoomAsync(1)).Error);
        Assert.Equal(2, test.Room.RunSequence);
        Assert.Equal(0, test.Room.RoundNumber);
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token, 0)).Error);
        var deadline = test.Room.NextRoundAvailableAtUtc;

        var (_, error) = await test.Service.CancelPreparationAsync(1, test.Token, 0, 1);

        Assert.Equal("StaleRound", error);
        Assert.True((await test.Db.RoomSlots.SingleAsync()).IsConfirmed);
        Assert.Equal(deadline, test.Room.NextRoundAvailableAtUtc);
        Assert.Equal(2, test.Room.RunSequence);
        Assert.Equal(0, test.Room.RoundNumber);
        Assert.Null((await test.Service.CancelPreparationAsync(1, test.Token, 0, 2)).Error);
        Assert.False((await test.Db.RoomSlots.SingleAsync()).IsConfirmed);
    }

    [Theory]
    [InlineData("invalid-token")]
    [InlineData("spectator")]
    [InlineData("dead")]
    [InlineData("auto")]
    public async Task CancelPreparation_RejectsCallersWithoutAnAliveManualParticipant(string scenario)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        await test.AddOtherMemberAsync();
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token)).Error);
        var slot = await test.Db.RoomSlots.SingleAsync(entry => entry.IsMainControl);
        string? token = test.Token;
        if (scenario == "invalid-token") token = "invalid-token";
        if (scenario == "spectator")
        {
            test.Db.AddRange(new User { Id = 3, UserName = "spectator", PasswordHash = "x" },
                new UserLoginSession { UserId = 3, Token = "spectator-token", CreatedAt = DateTime.UtcNow,
                    ExpireAt = DateTime.UtcNow.AddDays(1) });
            token = "spectator-token";
        }
        if (scenario == "dead") test.Character.Hp = 0;
        if (scenario == "auto") slot.IsAutoEnabled = true;
        await test.Db.SaveChangesAsync();

        var (_, error) = await test.Service.CancelPreparationAsync(1, token, 0, 1);

        Assert.NotNull(error);
        Assert.True(slot.IsConfirmed);
        Assert.Equal(0, test.Room.RoundNumber);
        Assert.Equal(scenario == "auto", slot.IsAutoEnabled);
        if (scenario == "invalid-token") Assert.Equal("Unauthorized", error);
        if (scenario == "spectator") Assert.Equal("NotInRoom", error);
    }

    [Fact]
    public async Task CancelPreparation_StoredAutoWithoutUnlockRemainsManual()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        await test.AddOtherMemberAsync();
        var slot = await test.Db.RoomSlots.SingleAsync(entry => entry.IsMainControl);
        slot.IsAutoEnabled = true;
        test.Db.CharacterBattleMilestones.RemoveRange(await test.Db.CharacterBattleMilestones
            .Where(milestone => milestone.CharacterId == test.Character.Id).ToListAsync());
        await test.Db.SaveChangesAsync();
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token)).Error);
        Assert.True((await test.GetRoomDetailAsync())!.CanCancelPreparation);

        Assert.Null((await test.Service.CancelPreparationAsync(1, test.Token, 0, 1)).Error);

        Assert.False(slot.IsConfirmed);
        Assert.True(slot.IsAutoEnabled);
    }

    [Theory]
    [InlineData(RoomStatus.WaveTransition)]
    [InlineData(RoomStatus.BattleOver)]
    public async Task CancelPreparation_RejectsNonPreparationPhases(RoomStatus phase)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1);
        if (phase == RoomStatus.BattleOver)
            Assert.Null((await test.Service.StartPreparationAsync(1, test.Token)).Error);
        else
        {
            test.Room.Status = RoomStatus.WaveTransition;
            test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(BattleRules.WaveTransitionSeconds);
            await test.Db.SaveChangesAsync();
        }

        var (_, error) = await test.Service.CancelPreparationAsync(1, test.Token, test.Room.RoundNumber, 1);

        Assert.Equal(phase.ToString(), error);
        Assert.Equal(phase, test.Room.Status);
        Assert.False((await test.GetRoomDetailAsync())!.CanCancelPreparation);
    }

    [Fact]
    public async Task CancelPreparation_RoomDetailsExposeOnlyTheCurrentUsersCancellableConfirmation()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        await test.AddOtherMemberAsync();
        Assert.False((await test.GetRoomDetailAsync())!.CanCancelPreparation);
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token)).Error);
        var prepared = await test.GetRoomDetailAsync();
        Assert.True(prepared!.CanCancelPreparation);
        Assert.False(prepared.CanPrepare);
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        var rooms = new RoomService(test.Db, new UserService(test.Db, progression, skills), progression,
            ConsumableTestFactory.Create(), skills, RewardTestFactory.CreateService(test.Db, progression));
        Assert.False((await rooms.GetRoomDetailAsync(1, "other-token"))!.CanCancelPreparation);
        Assert.False((await rooms.GetRoomDetailAsync(1))!.CanCancelPreparation);

        Assert.Null((await test.Service.CancelPreparationAsync(1, test.Token, 0, 1)).Error);

        var cancelled = await test.GetRoomDetailAsync();
        Assert.False(cancelled!.CanCancelPreparation);
        Assert.True(cancelled.CanPrepare);
    }

    [Fact]
    public async Task CancelPreparation_StaleContextCannotOverwriteASettledRound()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token)).Error);
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token, 1)).Error);
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        await using var staleDb = test.CreateDbContext();
        await staleDb.Rooms.LoadAsync();
        await staleDb.RoomSlots.LoadAsync();
        await staleDb.Characters.LoadAsync();
        await staleDb.Monsters.LoadAsync();
        var staleService = CreatePreparationCancellationService(staleDb);
        Assert.Null((await test.Service.SyncRoomAsync(1)).Error);
        Assert.Equal(2, test.Room.RoundNumber);
        var settledHp = test.Monster.Hp;
        var settledDeadline = test.Room.NextRoundAvailableAtUtc;

        var (result, error) = await staleService.CancelPreparationAsync(1, test.Token, 1, 1);

        Assert.Null(result);
        Assert.Equal("ConcurrencyConflict", error);
        await using var verificationDb = test.CreateDbContext();
        var persisted = await verificationDb.Rooms.SingleAsync();
        Assert.Equal(2, persisted.RoundNumber);
        Assert.Equal(settledDeadline, persisted.NextRoundAvailableAtUtc);
        Assert.Equal(settledHp, (await verificationDb.Monsters.SingleAsync()).Hp);
        Assert.False((await verificationDb.RoomSlots.SingleAsync()).IsConfirmed);
    }

    [Fact]
    public async Task CancelPreparation_CommittedCancellationCannotBeOverwrittenByAStaleSettlement()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token)).Error);
        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token, 1)).Error);
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        await using var workerDb = test.CreateDbContext();
        await workerDb.Rooms.LoadAsync();
        await workerDb.RoomSlots.LoadAsync();
        await workerDb.Characters.LoadAsync();
        await workerDb.Monsters.LoadAsync();
        var worker = CreatePreparationCancellationService(workerDb);
        var monsterHp = test.Monster.Hp;
        var characterHp = test.Character.Hp;
        Assert.Null((await test.Service.CancelPreparationAsync(1, test.Token, 1, 1)).Error);

        var (result, error) = await worker.SyncRoomAsync(1);

        Assert.Null(result);
        Assert.Equal("ConcurrencyConflict", error);
        await using var verificationDb = test.CreateDbContext();
        Assert.Equal(1, (await verificationDb.Rooms.SingleAsync()).RoundNumber);
        Assert.Equal(monsterHp, (await verificationDb.Monsters.SingleAsync()).Hp);
        Assert.Equal(characterHp, (await verificationDb.Characters.SingleAsync()).Hp);
        Assert.False((await verificationDb.RoomSlots.SingleAsync()).IsConfirmed);
    }

    private static BattleService CreatePreparationCancellationService(GameDbContext db)
    {
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        return new BattleService(db, new UserService(db, progression, skills), ConsumableTestFactory.Create(),
            skills, RewardTestFactory.CreateService(db, progression), soulImprintCatalog: SoulImprintTestFactory.Create());
    }
}
