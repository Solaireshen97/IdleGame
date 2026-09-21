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
    [Theory]
    [InlineData(ElementType.Fire, ElementType.Wind, 32, 95)]
    [InlineData(ElementType.Fire, ElementType.Water, 39, 92)]
    [InlineData(ElementType.Light, ElementType.Dark, 32, 95)]
    [InlineData(ElementType.Dark, ElementType.Light, 32, 95)]
    [InlineData(ElementType.Fire, ElementType.Fire, 35, 93)]
    public async Task MainWeaponAndMonsterElementsAffectBothSidesOfRound(
        ElementType playerElement, ElementType monsterElement, int monsterHp, int characterHp)
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Monster.Element = monsterElement;
        test.Db.CharacterWeapons.Add(new CharacterWeapon
        {
            CharacterId = test.Character.Id, WeaponCode = "test-main", Name = "Test Main",
            Element = playerElement, Attack = 20, MaxHp = 100, EquippedSlotIndex = WeaponRules.MainSlotIndex
        });
        await test.Db.SaveChangesAsync();

        var (round, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(monsterHp, round!.MonsterHp);
        Assert.Equal(characterHp, test.Character.Hp);
    }

    [Fact]
    public async Task OffhandElementDoesNotChangeDamageAndRoomShowsMainElement()
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Monster.Element = ElementType.Wind;
        test.Db.CharacterWeapons.Add(new CharacterWeapon
        {
            CharacterId = test.Character.Id, WeaponCode = "test-offhand", Name = "Test Offhand",
            Element = ElementType.Fire, Attack = 20, MaxHp = 100, EquippedSlotIndex = 2
        });
        await test.Db.SaveChangesAsync();

        var detail = await test.GetRoomDetailAsync();
        var (round, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Null(detail!.Slots.Single(slot => slot.CharacterId == test.Character.Id).CharacterElement);
        Assert.Equal(0, detail.Slots.Single(slot => slot.CharacterId == test.Character.Id).OutgoingElementModifierPercent);
        Assert.Equal(ElementType.Wind, detail.MonsterElement);
        Assert.Equal(35, round!.MonsterHp);
        Assert.Equal(93, test.Character.Hp);
    }

    [Fact]
    public async Task ElementAppliesToDamageSkillAndStacksWithGuardInSeparateZone()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 10);
        test.Monster.Element = ElementType.Wind;
        test.Db.CharacterWeapons.Add(new CharacterWeapon
        {
            CharacterId = test.Character.Id, WeaponCode = "test-main", Name = "Test Main",
            Element = ElementType.Fire, Attack = 10, MaxHp = 100, EquippedSlotIndex = 1
        });
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "knight-strike", autoUse: true);
        await test.AddSkillAsync(test.Character, 2, "knight-guard", autoUse: true, threshold: 100);
        var detail = await test.GetRoomDetailAsync();
        var fighter = Assert.Single(detail!.Slots, slot => slot.CharacterId == test.Character.Id);

        var (round, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(ElementType.Fire, fighter.CharacterElement);
        Assert.Equal(25, fighter.OutgoingElementModifierPercent);
        Assert.Equal(-25, fighter.IncomingElementModifierPercent);
        Assert.Equal(28, round!.MonsterHp); // floor((10-5)*1.25) + floor((10+8-5)*1.25) = 6 + 16
        Assert.Equal(98, test.Character.Hp); // floor((12-5)*0.75*0.5) = 2
        Assert.Contains(round.Logs, log => log.Contains("uses 盾击") && log.Contains("16 damage"));
        Assert.Contains(round.Logs, log => log.Contains("uses 守护"));
    }

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
        var userService = new UserService(test.Db, progression, SkillTestFactory.Create());
        var (roster, _) = await userService.GetCurrentCharactersAsync(test.Token);
        var room = await new RoomService(test.Db, userService, progression, ConsumableTestFactory.Create(), SkillTestFactory.Create(), RewardTestFactory.CreateService(test.Db, progression)).GetRoomDetailAsync(1, test.Token);
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
        var detail = await new RoomService(test.Db, new UserService(test.Db, progression, SkillTestFactory.Create()), progression, ConsumableTestFactory.Create(), SkillTestFactory.Create(), RewardTestFactory.CreateService(test.Db, progression)).GetRoomDetailAsync(1, test.Token);
        var mainSlot = detail!.Slots.Single(slot => slot.SlotIndex == 1);
        Assert.Equal(1, mainSlot.CharacterLevel);
        Assert.Equal(10, mainSlot.CharacterExperience);
        Assert.Equal(20, mainSlot.ExperienceToNextLevel);
        Assert.Equal(0, mainSlot.TalentPoints);
    }

    [Fact]
    public async Task VictoryDropsGoToEachParticipatingCharacterOnlyOnce()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        var second = await test.AddSlotAsync(2, "Mage");

        await test.Service.StartPreparationAsync(1, test.Token);
        await test.Service.SyncRoomAsync(1);
        await test.Service.StartPreparationAsync(1, test.Token);

        var stacks = await test.Db.CharacterItemStacks.OrderBy(stack => stack.CharacterId).ToListAsync();
        Assert.Equal(2, stacks.Count);
        Assert.Equal(new[] { test.Character.Id, second.Id }, stacks.Select(stack => stack.CharacterId));
        Assert.All(stacks, stack => Assert.Equal(1, stack.Quantity));
    }

    [Fact]
    public async Task VictorySettlesKillAndClearRewardsOnceAndReportsOnlyOwnedRewards()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        await test.AddOtherMemberAsync();

        var (victory, error) = await test.Service.StartPreparationAsync(1, test.Token);
        Assert.Null(error);
        Assert.Equal(RoomStatus.Preparing, victory!.RoomStatus);
        await test.Service.StartPreparationAsync(1, "other-token");
        await test.Service.SyncRoomAsync(1);

        Assert.Equal(13, (await test.Db.Users.FindAsync(1))!.Gold);
        Assert.Equal(13, (await test.Db.Users.FindAsync(2))!.Gold);
        Assert.Equal(10, test.Character.Experience);
        Assert.Equal("Victory", (await test.Db.RewardRuns.SingleAsync()).Status);
        Assert.Equal(2, await test.Db.RewardEvents.CountAsync());
        var progression = ProgressionTestFactory.Create();
        var rooms = new RoomService(test.Db, new UserService(test.Db, progression, SkillTestFactory.Create()),
            progression, ConsumableTestFactory.Create(), SkillTestFactory.Create(), RewardTestFactory.CreateService(test.Db, progression));
        var owner = await rooms.GetRoomDetailAsync(1, test.Token);
        var guest = await rooms.GetRoomDetailAsync(1, "other-token");
        Assert.Equal(13, owner!.Rewards!.Gold);
        Assert.Equal(10, owner.Rewards.Experience);
        Assert.Single(owner.Rewards.Items);
        Assert.Equal("Knight", owner.Rewards.Items[0].CharacterName);
        Assert.Equal(13, guest!.Rewards!.Gold);
        Assert.Single(guest.Rewards.Items);
        Assert.Equal("Mage", guest.Rewards.Items[0].CharacterName);
    }

    [Fact]
    public async Task GuaranteedKillWeaponDropCreatesUnequippedWeaponWithSkillSnapshot()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        var progression = ProgressionTestFactory.Create();
        var service = new BattleService(test.Db,
            new UserService(test.Db, progression, SkillTestFactory.Create()), ConsumableTestFactory.Create(),
            SkillTestFactory.Create(), RewardTestFactory.CreateService(test.Db, progression, guaranteedWeapon: true));

        var (victory, error) = await service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(RoomStatus.BattleOver, victory!.RoomStatus);
        var weapon = Assert.Single(await test.Db.CharacterWeapons.Include(item => item.Skills).ToListAsync());
        Assert.Equal(test.Character.Id, weapon.CharacterId);
        Assert.Equal("gale-bow", weapon.WeaponCode);
        Assert.Null(weapon.EquippedSlotIndex);
        Assert.Equal(7, weapon.Attack);
        Assert.Equal(2, Assert.Single(weapon.Skills).Level);
        Assert.Contains(await test.Db.RewardEntries.ToListAsync(), entry =>
            entry.Kind == "Weapon" && entry.EventKey == "monster:1" && entry.WeaponSnapshotJson is not null);
    }

    [Fact]
    public async Task DefeatSettlesEarlierKillPoolWithoutClearBonus()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 1, characterAttack: 1, monsterAttack: 100);
        var progression = ProgressionTestFactory.Create();
        var rewards = RewardTestFactory.CreateService(test.Db, progression);
        await rewards.RecordAsync(test.Room, "slime-field",
            [new RewardParticipant(1, test.Character)], "monster:earlier", isClear: false);
        await test.Db.SaveChangesAsync();
        Assert.Equal(0, (await test.Db.Users.FindAsync(1))!.Gold);

        var (defeat, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(RoomStatus.BattleOver, defeat!.RoomStatus);
        Assert.True(defeat.IsCharacterDead);
        Assert.Equal(3, (await test.Db.Users.FindAsync(1))!.Gold);
        Assert.Equal(2, test.Character.Experience);
        Assert.Empty(await test.Db.CharacterItemStacks.ToListAsync());
        Assert.Equal("Defeat", (await test.Db.RewardRuns.SingleAsync()).Status);
        Assert.Single(await test.Db.RewardEvents.ToListAsync());
    }

    [Fact]
    public async Task DismissingRoomSettlesPendingKillPool()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var progression = ProgressionTestFactory.Create();
        var rewards = RewardTestFactory.CreateService(test.Db, progression);
        await rewards.RecordAsync(test.Room, "slime-field",
            [new RewardParticipant(1, test.Character)], "monster:earlier", isClear: false);
        await test.Db.SaveChangesAsync();
        var rooms = new RoomService(test.Db,
            new UserService(test.Db, progression, SkillTestFactory.Create()), progression,
            ConsumableTestFactory.Create(), SkillTestFactory.Create(), rewards);

        var (success, error) = await rooms.DeleteRoomAsync(1, test.Token);

        Assert.True(success);
        Assert.Null(error);
        Assert.Null(await test.Db.Rooms.FindAsync(1));
        Assert.Equal(3, (await test.Db.Users.FindAsync(1))!.Gold);
        Assert.Equal(2, test.Character.Experience);
        Assert.Equal("Defeat", (await test.Db.RewardRuns.SingleAsync()).Status);
    }

    [Fact]
    public async Task SameKillEventCannotRollOrPayTwice()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var rewards = RewardTestFactory.CreateService(test.Db, ProgressionTestFactory.Create());
        var participants = new[] { new RewardParticipant(1, test.Character) };
        await rewards.RecordAsync(test.Room, "slime-field", participants, "monster:1", isClear: false);
        await rewards.RecordAsync(test.Room, "slime-field", participants, "monster:1", isClear: false);
        await test.Db.SaveChangesAsync();
        await rewards.SettleAsync(test.Room, false, DateTime.UtcNow, []);
        await test.Db.SaveChangesAsync();
        await rewards.RecordAsync(test.Room, "slime-field", participants, "monster:1", isClear: false);
        await rewards.SettleAsync(test.Room, false, DateTime.UtcNow, []);
        await test.Db.SaveChangesAsync();

        Assert.Single(await test.Db.RewardEvents.ToListAsync());
        Assert.Equal(2, await test.Db.RewardEntries.CountAsync());
        Assert.Equal(3, (await test.Db.Users.FindAsync(1))!.Gold);
        Assert.Equal(2, test.Character.Experience);
    }

    [Fact]
    public async Task VictoryDoesNotConsumeQueuedHealingPotion()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 50, characterAttack: 100);
        await test.AddPotionAsync(test.Character, quantity: 1, autoUse: true, threshold: 100);
        var (queued, _) = await test.Service.QueueConsumableAsync(
            new Game.Shared.Dtos.QueueConsumableRequest { RoomId = 1, CharacterId = test.Character.Id, ConsumableSlotIndex = 1 }, test.Token);
        Assert.True(queued);

        var (victory, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(RoomStatus.BattleOver, victory!.RoomStatus);
        Assert.DoesNotContain(victory.Logs, log => log.Contains("uses 小型治疗药水"));
        Assert.Equal(50, test.Character.Hp);
        Assert.Equal(2, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
    }

    [Fact]
    public async Task ManualConsumableUseWaitsForSettlementAndRespectsThreeFullRoundCooldown()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 60, characterAttack: 1, monsterAttack: 8);
        await test.AddPotionAsync(test.Character, quantity: 2, autoUse: false);

        var (queued, queueError) = await test.Service.QueueConsumableAsync(
            new Game.Shared.Dtos.QueueConsumableRequest { RoomId = 1, CharacterId = test.Character.Id, ConsumableSlotIndex = 1 }, test.Token);
        Assert.True(queued);
        Assert.Null(queueError);
        Assert.Equal(60, test.Character.Hp);
        Assert.Equal(2, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);

        var (first, firstError) = await test.Service.StartPreparationAsync(1, test.Token);
        Assert.Null(firstError);
        Assert.Contains(first!.Logs, log => log.Contains("uses 小型治疗药水"));
        Assert.Equal(77, test.Character.Hp);
        Assert.Equal(1, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Equal(1, test.Room.RoundNumber);

        for (var remaining = 3; remaining >= 1; remaining--)
        {
            var detail = await test.GetRoomDetailAsync();
            Assert.Equal(remaining, detail!.Slots.Single(slot => slot.CharacterId == test.Character.Id).Consumables.Single(slot => slot.SlotIndex == 1).CooldownRoundsRemaining);
            var (accepted, error) = await test.Service.QueueConsumableAsync(
                new Game.Shared.Dtos.QueueConsumableRequest { RoomId = 1, CharacterId = test.Character.Id, ConsumableSlotIndex = 1 }, test.Token);
            Assert.False(accepted);
            Assert.Equal("ConsumableCooldown", error);
            await test.CompleteCooldownAndPrepareAsync();
        }

        Assert.Equal(4, test.Room.RoundNumber);
        Assert.Equal(0, (await test.GetRoomDetailAsync())!.Slots.Single(slot => slot.CharacterId == test.Character.Id).Consumables.Single(slot => slot.SlotIndex == 1).CooldownRoundsRemaining);
        var (ready, readyError) = await test.Service.QueueConsumableAsync(
            new Game.Shared.Dtos.QueueConsumableRequest { RoomId = 1, CharacterId = test.Character.Id, ConsumableSlotIndex = 1 }, test.Token);
        Assert.True(ready);
        Assert.Null(readyError);
        await test.CompleteCooldownAndPrepareAsync();
        Assert.Equal(0, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Equal(5, test.Room.RoundNumber);
    }

    [Fact]
    public async Task AutomaticConsumableUseIsIndependentOfAutomaticPreparation()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 60, characterAttack: 1, monsterAttack: 8);
        await test.AddPotionAsync(test.Character, quantity: 1, autoUse: true, threshold: 70);

        var (round, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Contains(round!.Logs, log => log.Contains("uses 小型治疗药水"));
        Assert.False((await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == test.Character.Id)).IsAutoEnabled);
        Assert.Equal(77, test.Character.Hp);
        Assert.Equal(0, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
    }

    [Theory]
    [InlineData(40, 59, 1)]
    [InlineData(10, 49, 0)]
    public async Task AutomaticPotionChecksHealthAfterHealingSkill(int startingHp, int endingHp, int remainingPotions)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: startingHp, characterAttack: 1,
            characterDefense: 99, monsterAttack: 1);
        test.Character.ProfessionCode = "cleric";
        await test.AddSkillAsync(test.Character, 1, "cleric-heal", autoUse: true, threshold: 70);
        await test.AddPotionAsync(test.Character, quantity: 1, autoUse: true, threshold: 50);

        var (round, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Contains(round!.Logs, log => log.Contains("uses 治疗术"));
        Assert.Equal(remainingPotions == 0, round.Logs.Any(log => log.Contains("uses 小型治疗药水")));
        Assert.Equal(endingHp, test.Character.Hp);
        Assert.Equal(remainingPotions, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
    }

    [Fact]
    public async Task MixedPartyUsesEachCharactersOwnPotionWhenBothPlayersPrepare()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 60, characterAttack: 1, monsterAttack: 8);
        var guestSlot = await test.AddOtherMemberAsync();
        var guest = await test.Db.Characters.SingleAsync(character => character.Id == guestSlot.CharacterId);
        guest.Hp = 40;
        guest.Attack = 1;
        await test.Db.SaveChangesAsync();
        await test.AddPotionAsync(test.Character, quantity: 1, autoUse: false);
        await test.AddPotionAsync(guest, quantity: 1, autoUse: true, threshold: 50);

        var (queued, queueError) = await test.Service.QueueConsumableAsync(
            new Game.Shared.Dtos.QueueConsumableRequest { RoomId = 1, CharacterId = test.Character.Id, ConsumableSlotIndex = 1 }, test.Token);
        var (waiting, ownerError) = await test.Service.StartPreparationAsync(1, test.Token);
        Assert.True(queued);
        Assert.Null(queueError);
        Assert.Null(ownerError);
        Assert.Equal(RoomStatus.Preparing, waiting!.RoomStatus);
        Assert.All(await test.Db.CharacterItemStacks.ToListAsync(), stack => Assert.Equal(1, stack.Quantity));

        var (round, guestError) = await test.Service.StartPreparationAsync(1, "other-token");

        Assert.Null(guestError);
        Assert.Equal(RoomStatus.Cooldown, round!.RoomStatus);
        Assert.Equal(2, round.Logs.Count(log => log.Contains("uses 小型治疗药水")));
        Assert.Equal(77, test.Character.Hp);
        Assert.Equal(60, guest.Hp);
        Assert.All(await test.Db.CharacterItemStacks.ToListAsync(), stack => Assert.Equal(0, stack.Quantity));
        Assert.Equal(2, await test.Db.BattleConsumableCooldowns.CountAsync());
    }

    [Fact]
    public async Task ManualSkillsCanBothFireAfterAttackAndBeforeMonsterCounterattack()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 60, characterAttack: 1);
        await test.AddSkillAsync(test.Character, 1, "knight-strike", autoUse: false);
        await test.AddSkillAsync(test.Character, 2, "knight-guard", autoUse: false);
        var (firstQueued, firstError) = await test.Service.QueueSkillAsync(
            new Game.Shared.Dtos.QueueSkillRequest { RoomId = 1, CharacterId = 1, SkillSlotIndex = 1, IsQueued = true }, test.Token);
        var (secondQueued, secondError) = await test.Service.QueueSkillAsync(
            new Game.Shared.Dtos.QueueSkillRequest { RoomId = 1, CharacterId = 1, SkillSlotIndex = 2, IsQueued = true }, test.Token);

        var (round, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.True(firstQueued);
        Assert.True(secondQueued);
        Assert.Null(firstError);
        Assert.Null(secondError);
        Assert.Null(error);
        Assert.Equal(RoomStatus.Cooldown, round!.RoomStatus);
        Assert.Equal(45, round.MonsterHp);
        Assert.Equal(57, test.Character.Hp);
        var strike = round.Logs.FindIndex(log => log.Contains("uses 盾击"));
        var guard = round.Logs.FindIndex(log => log.Contains("uses 守护"));
        var counterattack = round.Logs.FindIndex(log => log.Contains("Slime attacks"));
        Assert.True(strike > 0 && guard > strike && counterattack > guard);
        Assert.Equal(new[] { 3, 4 }, (await test.Db.BattleSkillCooldowns.OrderBy(entry => entry.SkillCode).ToListAsync())
            .Select(entry => entry.ReadyAtRound).OrderBy(round => round));
        Assert.Equal(0, (await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == 1)).PendingSkillSlotMask);
    }

    [Fact]
    public async Task AutoSkillsSkipUnsatisfiedConditionAndContinueThroughFiveSlots()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1);
        await test.AddSkillAsync(test.Character, 1, "knight-guard", autoUse: true, threshold: 70);
        await test.AddSkillAsync(test.Character, 5, "knight-strike", autoUse: true);

        var (round, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Contains(round!.Logs, log => log.Contains("uses 盾击"));
        Assert.DoesNotContain(round.Logs, log => log.Contains("uses 守护"));
        Assert.Single(await test.Db.BattleSkillCooldowns.ToListAsync());
    }

    [Fact]
    public async Task GuardSkillsStopUsingCooldownOncePartyProtectionReachesItsCap()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 60, characterAttack: 1, monsterAttack: 20);
        var second = await test.AddSlotAsync(2, "Second", hp: 60, attack: 1);
        var third = await test.AddSlotAsync(3, "Third", hp: 60, attack: 1);
        test.Monster.Hp = test.Monster.MaxHp = 100;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "knight-guard", autoUse: true, threshold: 70);
        await test.AddSkillAsync(second, 1, "knight-guard", autoUse: true, threshold: 70);
        await test.AddSkillAsync(third, 1, "knight-guard", autoUse: true, threshold: 70);

        var (round, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(2, round!.Logs.Count(log => log.Contains("uses 守护")));
        Assert.Equal(2, await test.Db.BattleSkillCooldowns.CountAsync());
        Assert.DoesNotContain(await test.Db.BattleSkillCooldowns.ToListAsync(), cooldown => cooldown.CharacterId == third.Id);
    }

    [Fact]
    public async Task SwappingSkillSlotsChangesAutomaticCastOrder()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 60, characterAttack: 1);
        await test.AddSkillAsync(test.Character, 1, "knight-guard", autoUse: true, threshold: 70);
        await test.AddSkillAsync(test.Character, 2, "knight-strike", autoUse: true);
        var progression = ProgressionTestFactory.Create();
        var skillCatalog = SkillTestFactory.Create();
        var userService = new UserService(test.Db, progression, skillCatalog);
        var skillService = new SkillService(test.Db, userService, skillCatalog,
            new TalentService(test.Db, userService, skillCatalog));

        var (configuration, swapError) = await skillService.SwapSlotsAsync(test.Token, 1,
            new Game.Shared.Dtos.Characters.SwapSkillSlotsRequest { FromSlotIndex = 1, ToSlotIndex = 2 });
        var (round, battleError) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(swapError);
        Assert.Null(battleError);
        Assert.Equal("knight-strike", configuration!.Slots[0].SkillCode);
        Assert.Equal("knight-guard", configuration.Slots[1].SkillCode);
        var strike = round!.Logs.FindIndex(log => log.Contains("uses 盾击"));
        var guard = round.Logs.FindIndex(log => log.Contains("uses 守护"));
        Assert.True(strike >= 0 && guard > strike);
    }

    [Fact]
    public async Task SkillLoadoutRejectsAnotherProfessionsSkillAndLocksDuringCombat()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var progression = ProgressionTestFactory.Create();
        var skillCatalog = SkillTestFactory.Create();
        var userService = new UserService(test.Db, progression, skillCatalog);
        var skillService = new SkillService(test.Db, userService, skillCatalog,
            new TalentService(test.Db, userService, skillCatalog));
        var (foreign, foreignError) = await skillService.SetSlotAsync(test.Token, 1, 1,
            new Game.Shared.Dtos.Characters.SetSkillSlotRequest { SkillCode = "cleric-heal" });
        Assert.Null(foreign);
        Assert.Equal("SkillNotLearned", foreignError);

        test.Room.Status = RoomStatus.Preparing;
        await test.Db.SaveChangesAsync();
        var (locked, lockedError) = await skillService.SetSlotAsync(test.Token, 1, 1,
            new Game.Shared.Dtos.Characters.SetSkillSlotRequest { SkillCode = "knight-strike" });
        Assert.Null(locked);
        Assert.Equal("LoadoutLocked", lockedError);
    }

    [Fact]
    public async Task ClericCanHealFrontAllyAndCastSecondSkillInSameRound()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 60, characterAttack: 1);
        var cleric = await test.AddSlotAsync(2, "Healer", attack: 1);
        cleric.ProfessionCode = "cleric";
        test.Monster.Hp = test.Monster.MaxHp = 100;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(cleric, 1, "cleric-heal", autoUse: true, threshold: 70);
        await test.AddSkillAsync(cleric, 2, "cleric-smite", autoUse: true);

        var (round, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Contains(round!.Logs, log => log.Contains("uses 治疗术 on Slot 1"));
        Assert.Contains(round.Logs, log => log.Contains("uses 圣光击"));
        Assert.Equal(73, test.Character.Hp);
        Assert.Equal(2, await test.Db.BattleSkillCooldowns.CountAsync());
    }

    [Fact]
    public async Task PartyManualSkillsResolveBeforeAnotherCharactersAutomaticSkills()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 60, characterAttack: 1);
        var cleric = await test.AddSlotAsync(2, "Healer", attack: 1);
        cleric.ProfessionCode = "cleric";
        test.Monster.Hp = test.Monster.MaxHp = 100;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "knight-strike", autoUse: true);
        await test.AddSkillAsync(cleric, 1, "cleric-heal", autoUse: false);
        var (queued, queueError) = await test.Service.QueueSkillAsync(
            new Game.Shared.Dtos.QueueSkillRequest { RoomId = 1, CharacterId = cleric.Id, SkillSlotIndex = 1, IsQueued = true }, test.Token);

        var (round, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.True(queued);
        Assert.Null(queueError);
        Assert.Null(error);
        var heal = round!.Logs.FindIndex(log => log.Contains("uses 治疗术"));
        var strike = round.Logs.FindIndex(log => log.Contains("uses 盾击"));
        Assert.True(heal >= 0 && strike > heal);
    }

    [Fact]
    public async Task UnlearnedTalentSkillCannotFireEvenIfEquippedDirectlyInDatabase()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1);
        await test.AddSkillAsync(test.Character, 3, "knight-break", autoUse: true);
        var initialDetail = await test.GetRoomDetailAsync();
        var skillSlot = initialDetail!.Slots.Single(slot => slot.CharacterId == test.Character.Id).Skills.Single(slot => slot.SlotIndex == 3);
        Assert.Null(skillSlot.SkillCode);
        Assert.False(skillSlot.AutoUseEnabled);

        var (firstRound, firstError) = await test.Service.StartPreparationAsync(1, test.Token);
        Assert.Null(firstError);
        Assert.DoesNotContain(firstRound!.Logs, log => log.Contains("uses 破甲斩"));

        test.Db.CharacterSkillTalents.Add(new CharacterSkillTalent
        {
            CharacterId = test.Character.Id, NodeCode = "knight-vanguard", PointsSpent = 1
        });
        test.Character.AttackTalentRank = 1;
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        test.Room.Version++;
        await test.Db.SaveChangesAsync();

        var (secondRound, secondError) = await test.Service.StartPreparationAsync(1, test.Token);
        Assert.Null(secondError);
        Assert.Contains(secondRound!.Logs, log => log.Contains("uses 破甲斩"));
    }

    [Fact]
    public async Task ManualUseCanOverrideAutomaticHpThreshold()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 60, characterAttack: 1, monsterAttack: 5);
        await test.AddPotionAsync(test.Character, quantity: 1, autoUse: true, threshold: 50);

        await test.Service.StartPreparationAsync(1, test.Token);
        Assert.Equal(59, test.Character.Hp);
        Assert.Equal(1, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);

        var (queued, queueError) = await test.Service.QueueConsumableAsync(
            new Game.Shared.Dtos.QueueConsumableRequest { RoomId = 1, CharacterId = test.Character.Id, ConsumableSlotIndex = 1 }, test.Token);
        Assert.True(queued);
        Assert.Null(queueError);
        await test.CompleteCooldownAndPrepareAsync();

        Assert.Equal(78, test.Character.Hp);
        Assert.Equal(0, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
    }

    [Fact]
    public async Task RepeatBattleRestartClearsConsumableCooldownWithoutRestoringStock()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 60, characterAttack: 30, monsterAttack: 8);
        test.Room.IsRepeatBattle = true;
        await test.AddPotionAsync(test.Character, quantity: 1, autoUse: false);
        await test.Db.SaveChangesAsync();

        var (queued, _) = await test.Service.QueueConsumableAsync(
            new Game.Shared.Dtos.QueueConsumableRequest { RoomId = 1, CharacterId = test.Character.Id, ConsumableSlotIndex = 1 }, test.Token);
        Assert.True(queued);
        await test.Service.StartPreparationAsync(1, test.Token);
        Assert.Equal(4, (await test.Db.BattleConsumableCooldowns.SingleAsync()).ReadyAtRound);
        Assert.Equal(0, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);

        await test.CompleteCooldownAndPrepareAsync();
        Assert.Equal(RoomStatus.BattleOver, test.Room.Status);
        Assert.Equal(1, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity); // victory drop
        test.Room.BattleEndedAtUtc = DateTime.UtcNow.AddSeconds(-31);
        await test.Db.SaveChangesAsync();
        await test.Service.SyncRoomAsync(1);

        Assert.Equal(0, test.Room.RoundNumber);
        Assert.Equal(0, (await test.Db.BattleConsumableCooldowns.SingleAsync()).ReadyAtRound);
        Assert.Equal(1, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Equal(100, test.Character.Hp);
    }

    [Fact]
    public async Task ConcurrentRepeatRestartAwardsExactlyOneNewVictoryDrop()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        test.Room.IsRepeatBattle = true;
        var mainSlot = await test.Db.RoomSlots.SingleAsync(slot => slot.IsMainControl);
        mainSlot.IsAutoEnabled = true;
        await test.Db.SaveChangesAsync();
        await test.Service.StartPreparationAsync(1, test.Token);
        Assert.Equal(1, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
        test.Room.BattleEndedAtUtc = DateTime.UtcNow.AddSeconds(-31);
        await test.Db.SaveChangesAsync();

        await using var firstDb = test.CreateDbContext();
        await using var secondDb = test.CreateDbContext();
        var progression = ProgressionTestFactory.Create();
        var catalog = ConsumableTestFactory.Create();
        var first = new BattleService(firstDb, new UserService(firstDb, progression, SkillTestFactory.Create()), catalog, SkillTestFactory.Create(), RewardTestFactory.CreateService(firstDb, progression));
        var second = new BattleService(secondDb, new UserService(secondDb, progression, SkillTestFactory.Create()), catalog, SkillTestFactory.Create(), RewardTestFactory.CreateService(secondDb, progression));
        var results = await Task.WhenAll(first.SyncRoomAsync(1), second.SyncRoomAsync(1));

        Assert.All(results, result => Assert.True(result.Error is null or "ConcurrencyConflict"));
        await using var verificationDb = test.CreateDbContext();
        Assert.Equal(2, (await verificationDb.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Equal(26, (await verificationDb.Users.SingleAsync()).Gold);
        Assert.Equal(new[] { 1, 2 }, (await verificationDb.RewardRuns.OrderBy(run => run.Sequence).ToListAsync()).Select(run => run.Sequence));
        Assert.Equal(1, (await verificationDb.Rooms.SingleAsync()).RoundNumber);
        Assert.Equal(RoomStatus.BattleOver, (await verificationDb.Rooms.SingleAsync()).Status);
    }

    [Fact]
    public async Task ManualConsumableCannotBeQueuedForAnotherPlayersCharacter()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 60);
        await test.AddOtherMemberAsync();
        await test.AddPotionAsync(test.Character, quantity: 1, autoUse: false);

        var (success, error) = await test.Service.QueueConsumableAsync(
            new Game.Shared.Dtos.QueueConsumableRequest { RoomId = 1, CharacterId = test.Character.Id, ConsumableSlotIndex = 1 }, "other-token");

        Assert.False(success);
        Assert.Equal("NotCharacterOwner", error);
        Assert.Equal(1, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
    }

    [Fact]
    public async Task RoomDetailOnlyRevealsTheCurrentPlayersConsumableStock()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var guest = await test.AddOtherMemberAsync();
        await test.AddPotionAsync(test.Character, quantity: 2, autoUse: false);
        var guestCharacter = await test.Db.Characters.SingleAsync(character => character.Id == guest.CharacterId);
        await test.AddPotionAsync(guestCharacter, quantity: 5, autoUse: true);
        var progression = ProgressionTestFactory.Create();
        var roomService = new RoomService(test.Db, new UserService(test.Db, progression, SkillTestFactory.Create()), progression, ConsumableTestFactory.Create(), SkillTestFactory.Create(), RewardTestFactory.CreateService(test.Db, progression));

        var ownerView = await roomService.GetRoomDetailAsync(1, test.Token);
        var guestView = await roomService.GetRoomDetailAsync(1, "other-token");

        Assert.Equal(2, ownerView!.Slots.Single(slot => slot.CharacterId == test.Character.Id).Consumables[0].Quantity);
        Assert.Empty(ownerView.Slots.Single(slot => slot.CharacterId == guestCharacter.Id).Consumables);
        Assert.Equal(5, guestView!.Slots.Single(slot => slot.CharacterId == guestCharacter.Id).Consumables[0].Quantity);
        Assert.Empty(guestView.Slots.Single(slot => slot.CharacterId == test.Character.Id).Consumables);
    }

    [Fact]
    public async Task ConsumableLoadoutsAndInventoriesBelongToIndividualCharacters()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var second = await test.AddSlotAsync(2, "Mage");
        await test.AddPotionAsync(test.Character, quantity: 3, autoUse: false);
        var service = test.CreateConsumableService();

        var (secondLoadout, setError) = await service.SetSlotAsync(test.Token, second.Id, 1,
            new Game.Shared.Dtos.Characters.SetConsumableSlotRequest
            {
                ItemCode = "minor-healing-potion",
                AutoUseEnabled = true,
                AutoHpThresholdPercent = 70
            });
        var (firstLoadout, firstError) = await service.GetAsync(test.Token, test.Character.Id);

        Assert.Null(setError);
        Assert.Null(firstError);
        Assert.Equal(0, Assert.Single(secondLoadout!.Items).Quantity);
        Assert.Equal(3, Assert.Single(firstLoadout!.Items).Quantity);
        Assert.True(secondLoadout.Slots.Single(slot => slot.SlotIndex == 1).AutoUseEnabled);
        Assert.False(firstLoadout.Slots.Single(slot => slot.SlotIndex == 1).AutoUseEnabled);
    }

    [Fact]
    public async Task ConsumableLoadoutRejectsDuplicateItemAndChangesDuringBattle()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var service = test.CreateConsumableService();
        var request = new Game.Shared.Dtos.Characters.SetConsumableSlotRequest { ItemCode = "minor-healing-potion" };
        var (_, firstError) = await service.SetSlotAsync(test.Token, test.Character.Id, 1, request);
        var (_, duplicateError) = await service.SetSlotAsync(test.Token, test.Character.Id, 2, request);
        await test.Service.StartPreparationAsync(1, test.Token);
        var (_, lockedError) = await service.SetSlotAsync(test.Token, test.Character.Id, 1, request);

        Assert.Null(firstError);
        Assert.Equal("ConsumableAlreadyEquipped", duplicateError);
        Assert.Equal("LoadoutLocked", lockedError);
    }

    [Fact]
    public async Task ChangingConsumableSlotClearsItsQueuedUse()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 60);
        await test.AddPotionAsync(test.Character, quantity: 1, autoUse: false);
        var (queued, queueError) = await test.Service.QueueConsumableAsync(
            new Game.Shared.Dtos.QueueConsumableRequest
            {
                RoomId = 1, CharacterId = test.Character.Id, ConsumableSlotIndex = 1
            }, test.Token);
        Assert.True(queued);
        Assert.Null(queueError);
        var roomSlot = await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == test.Character.Id);
        Assert.Equal(1, roomSlot.PendingConsumableSlotIndex);

        var (_, error) = await test.CreateConsumableService().SetSlotAsync(test.Token, test.Character.Id, 1,
            new Game.Shared.Dtos.Characters.SetConsumableSlotRequest { ItemCode = null });

        Assert.Null(error);
        Assert.Null(roomSlot.PendingConsumableSlotIndex);
        Assert.Equal(1, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
    }

    [Fact]
    public async Task ConsumableLoadoutRejectsAnotherPlayersCharacter()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var guestSlot = await test.AddOtherMemberAsync();
        var service = test.CreateConsumableService();

        var (response, error) = await service.SetSlotAsync(test.Token, guestSlot.CharacterId!.Value, 1,
            new Game.Shared.Dtos.Characters.SetConsumableSlotRequest { ItemCode = "minor-healing-potion" });

        Assert.Null(response);
        Assert.Equal("NotOwner", error);
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
        await using var test = await BattleTestContext.CreateAsync(characterHp: 60);
        await test.AddPotionAsync(test.Character, quantity: 1, autoUse: true, threshold: 70);
        await using var firstDb = test.CreateDbContext();
        await using var secondDb = test.CreateDbContext();
        var progression = ProgressionTestFactory.Create();
        var catalog = ConsumableTestFactory.Create();
        var firstService = new BattleService(firstDb, new UserService(firstDb, progression, SkillTestFactory.Create()), catalog, SkillTestFactory.Create(), RewardTestFactory.CreateService(firstDb, progression));
        var secondService = new BattleService(secondDb, new UserService(secondDb, progression, SkillTestFactory.Create()), catalog, SkillTestFactory.Create(), RewardTestFactory.CreateService(secondDb, progression));

        var results = await Task.WhenAll(firstService.StartPreparationAsync(1, test.Token), secondService.StartPreparationAsync(1, test.Token));
        Assert.Single(results.Where(result => result.Error is null));
        await using var verificationDb = test.CreateDbContext();
        Assert.Equal(0, (await verificationDb.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Equal(73, (await verificationDb.Characters.SingleAsync()).Hp);
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
            Service = new BattleService(db, new UserService(db, progression, SkillTestFactory.Create()), ConsumableTestFactory.Create(), SkillTestFactory.Create(), RewardTestFactory.CreateService(db, progression));
        }

        public string Token => "token";
        public GameDbContext Db { get; }
        public Room Room { get; }
        public Character Character { get; }
        public Monster Monster { get; }
        public BattleService Service { get; }

        public ConsumableService CreateConsumableService()
        {
            var progression = ProgressionTestFactory.Create();
            return new ConsumableService(Db, new UserService(Db, progression, SkillTestFactory.Create()), ConsumableTestFactory.Create());
        }

        public async Task AddPotionAsync(Character character, int quantity, bool autoUse, int threshold = 50)
        {
            Db.CharacterItemStacks.Add(new CharacterItemStack { CharacterId = character.Id, ItemCode = "minor-healing-potion", Quantity = quantity });
            Db.CharacterConsumableSlots.Add(new CharacterConsumableSlot
            {
                CharacterId = character.Id,
                SlotIndex = 1,
                ItemCode = "minor-healing-potion",
                AutoUseEnabled = autoUse,
                AutoHpThresholdPercent = threshold
            });
            await Db.SaveChangesAsync();
        }

        public async Task AddSkillAsync(Character character, int slotIndex, string code, bool autoUse, int threshold = 70)
        {
            Db.CharacterSkillSlots.Add(new CharacterSkillSlot
            {
                CharacterId = character.Id,
                SlotIndex = slotIndex,
                SkillCode = code,
                AutoUseEnabled = autoUse,
                AutoHpThresholdPercent = threshold
            });
            await Db.SaveChangesAsync();
        }

        public Task<Game.Shared.Dtos.RoomDetailResponse?> GetRoomDetailAsync()
        {
            var progression = ProgressionTestFactory.Create();
            return new RoomService(Db, new UserService(Db, progression, SkillTestFactory.Create()), progression, ConsumableTestFactory.Create(), SkillTestFactory.Create(), RewardTestFactory.CreateService(Db, progression)).GetRoomDetailAsync(Room.Id, Token);
        }

        public async Task CompleteCooldownAndPrepareAsync()
        {
            Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
            Room.Version++;
            await Db.SaveChangesAsync();
            var (result, error) = await Service.StartPreparationAsync(Room.Id, Token);
            Assert.Null(error);
            Assert.NotNull(result);
        }

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
