using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Fact]
    public async Task SoulImprintWaitsForInitialCooldownThenAutoCastsAndStartsOwnCooldown()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 20, monsterAttack: 1);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Db.CharacterSoulImprints.Add(new CharacterSoulImprint
        {
            CharacterId = test.Character.Id, SoulImprintCode = "deep-core",
            EquippedSlotIndex = SoulImprintRules.SlotIndex, AutoUseEnabled = true
        });
        await test.Db.SaveChangesAsync();

        var before = await test.GetRoomDetailAsync();
        Assert.Equal(3, Assert.Single(before!.Slots).SoulImprint!.CooldownRoundsRemaining);
        var (first, firstError) = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);
        await test.CompleteCooldownAndPrepareAsync();
        await test.CompleteCooldownAndPrepareAsync();

        Assert.Null(firstError);
        Assert.DoesNotContain(first!.Logs, log => log.Contains("释放魂印"));
        var ready = await test.GetRoomDetailAsync();
        Assert.Equal(0, Assert.Single(ready!.Slots).SoulImprint!.CooldownRoundsRemaining);

        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        test.Room.Version++;
        await test.Db.SaveChangesAsync();
        var (cast, castError) = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(castError);
        Assert.Contains(cast!.Logs, log => log.Contains("释放魂印") && log.Contains("深岩监工的震核"));
        var cooldown = await test.Db.BattleSkillCooldowns.SingleAsync(item =>
            item.SkillCode == SoulImprintRules.CooldownCode("deep-core"));
        Assert.Equal(12, cooldown.ReadyAtRound);
        var after = await test.GetRoomDetailAsync();
        Assert.Equal(8, Assert.Single(after!.Slots).SoulImprint!.CooldownRoundsRemaining);
    }

    [Fact]
    public async Task SoulImprintCannotBeQueuedBeforeInitialCooldown()
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Db.CharacterSoulImprints.Add(new CharacterSoulImprint
        {
            CharacterId = test.Character.Id, SoulImprintCode = "deep-core",
            EquippedSlotIndex = SoulImprintRules.SlotIndex
        });
        await test.Db.SaveChangesAsync();

        var (queued, error) = await test.Service.QueueSoulImprintAsync(new QueueSoulImprintRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, IsQueued = true
        }, test.Token);

        Assert.False(queued);
        Assert.Equal("SoulImprintCooldown", error);
        Assert.False((await test.Db.RoomSlots.SingleAsync()).IsSoulImprintQueued);
    }

    [Fact]
    public async Task EchoSoulImprintAddsDefenseIgnoringFollowUpDamage()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 20, monsterAttack: 1);
        var (service, _) = CreateProductionSoulBattleService(test);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Room.RoundNumber = 4;
        test.Db.CharacterSoulImprints.Add(new CharacterSoulImprint
        {
            CharacterId = test.Character.Id, SoulImprintCode = "plague-widow-essence",
            EquippedSlotIndex = SoulImprintRules.SlotIndex, AutoUseEnabled = true
        });
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("魂印毒蚀") && log.Contains("无视防御"));
        Assert.True(test.Monster.Hp < 950);
    }

    [Fact]
    public async Task ArmorBreakSoulImprintAppliesItsConfiguredStatus()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 20, monsterAttack: 1);
        var (service, _) = CreateProductionSoulBattleService(test);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Room.RoundNumber = 4;
        test.Db.CharacterSoulImprints.Add(new CharacterSoulImprint
        {
            CharacterId = test.Character.Id, SoulImprintCode = "deep-overseer-core",
            EquippedSlotIndex = SoulImprintRules.SlotIndex, AutoUseEnabled = true
        });
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("深岩监工的震核") && log.Contains("土属性伤害"));
        var status = await test.Db.BattleStatusEffects.SingleAsync(effect =>
            effect.TargetType == "Monster" && effect.EffectCode == "armor-break");
        Assert.True(status.ExpiresAfterRound >= test.Room.RoundNumber);
    }

    [Fact]
    public async Task CooldownSoulImprintOnlyReducesClassSkillCooldowns()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        var (service, _) = CreateProductionSoulBattleService(test);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Room.RoundNumber = 4;
        test.Db.CharacterSoulImprints.Add(new CharacterSoulImprint
        {
            CharacterId = test.Character.Id, SoulImprintCode = "storm-matriarch-plume",
            EquippedSlotIndex = SoulImprintRules.SlotIndex, AutoUseEnabled = true
        });
        test.Db.BattleSkillCooldowns.Add(new BattleSkillCooldown
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillCode = "sword-slash", ReadyAtRound = 10
        });
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("剩余冷却缩短 2 回合"));
        Assert.Equal(8, (await test.Db.BattleSkillCooldowns.SingleAsync(cooldown =>
            cooldown.SkillCode == "sword-slash")).ReadyAtRound);
        Assert.Equal(12, (await test.Db.BattleSkillCooldowns.SingleAsync(cooldown =>
            cooldown.SkillCode == SoulImprintRules.CooldownCode("storm-matriarch-plume"))).ReadyAtRound);
    }

    [Fact]
    public async Task HealingSoulImprintHealsPartyAndCleansesDebuff()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 40, characterAttack: 1, monsterAttack: 1);
        var (service, monsterCombat) = CreateProductionSoulBattleService(test);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Room.RoundNumber = 4;
        test.Db.CharacterSoulImprints.Add(new CharacterSoulImprint
        {
            CharacterId = test.Character.Id, SoulImprintCode = "dawn-core-prism",
            EquippedSlotIndex = SoulImprintRules.SlotIndex, AutoUseEnabled = true
        });
        await monsterCombat.ApplyStatusAsync(test.Room, "Character", test.Character.Id, "poison", 2, [], test.Character.Name);
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("恢复 24 点生命值"));
        Assert.Contains(result.Logs, log => log.Contains("移除了") && log.Contains("中毒"));
        Assert.True(test.Character.Hp > 40);
        Assert.Empty(await test.Db.BattleStatusEffects.Where(effect => effect.TargetType == "Character").ToListAsync());
    }

    [Fact]
    public async Task HealingSoulImprintAutoWaitsForMeaningfulDamage()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 90, characterAttack: 1, monsterAttack: 1);
        var (service, _) = CreateProductionSoulBattleService(test);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Room.RoundNumber = 4;
        test.Db.CharacterSoulImprints.Add(new CharacterSoulImprint
        {
            CharacterId = test.Character.Id, SoulImprintCode = "dawn-core-prism",
            EquippedSlotIndex = SoulImprintRules.SlotIndex, AutoUseEnabled = true
        });
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.DoesNotContain(result!.Logs, log => log.Contains("黎明守卫的棱晶"));
        Assert.DoesNotContain(await test.Db.BattleSkillCooldowns.ToListAsync(), cooldown =>
            cooldown.SkillCode == SoulImprintRules.CooldownCode("dawn-core-prism"));
    }

    [Fact]
    public async Task HealingSoulImprintCleansesOnlyTheConfiguredCountPerAlly()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 40, characterAttack: 1, monsterAttack: 1);
        var (service, monsterCombat) = CreateProductionSoulBattleService(test);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Room.RoundNumber = 4;
        test.Db.CharacterSoulImprints.Add(new CharacterSoulImprint
        {
            CharacterId = test.Character.Id, SoulImprintCode = "dawn-core-prism",
            EquippedSlotIndex = SoulImprintRules.SlotIndex, AutoUseEnabled = true
        });
        await monsterCombat.ApplyStatusAsync(test.Room, "Character", test.Character.Id, "poison", 2, [], test.Character.Name);
        await monsterCombat.ApplyStatusAsync(test.Room, "Character", test.Character.Id, "armor-break", 2, [], test.Character.Name);
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Single(result!.Logs, log => log.Contains("黎明守卫的棱晶") && log.Contains("移除了"));
        Assert.Single(await test.Db.BattleStatusEffects.Where(effect => effect.TargetType == "Character").ToListAsync());
    }

    [Fact]
    public async Task GuardSoulImprintReducesTheCurrentMonsterAttack()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 20);
        var (service, _) = CreateProductionSoulBattleService(test);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Room.RoundNumber = 4;
        test.Db.CharacterSoulImprints.Add(new CharacterSoulImprint
        {
            CharacterId = test.Character.Id, SoulImprintCode = "frost-king-heart",
            EquippedSlotIndex = SoulImprintRules.SlotIndex, AutoUseEnabled = true
        });
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("冻心屏障"));
        Assert.Equal(90, test.Character.Hp);
    }

    [Fact]
    public async Task GuardSoulImprintAutoWaitsUntilTheOwnerWillBeAttacked()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 20);
        var (service, _) = CreateProductionSoulBattleService(test);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Room.RoundNumber = 4;
        test.Db.CharacterSoulImprints.Add(new CharacterSoulImprint
        {
            CharacterId = test.Character.Id, SoulImprintCode = "frost-king-heart",
            EquippedSlotIndex = SoulImprintRules.SlotIndex, AutoUseEnabled = true
        });
        test.Db.MonsterIntents.Add(new MonsterIntent
        {
            RoomId = test.Room.Id, RunSequence = test.Room.RunSequence, RoundNumber = test.Room.RoundNumber,
            MonsterId = test.Monster.Id, ActionType = "BasicAttack", TargetType = "Self",
            CreatedAtUtc = DateTime.UtcNow
        });
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.DoesNotContain(result!.Logs, log => log.Contains("霜脉之王的冻心"));
        Assert.DoesNotContain(await test.Db.BattleSkillCooldowns.ToListAsync(), cooldown =>
            cooldown.SkillCode == SoulImprintRules.CooldownCode("frost-king-heart"));
    }

    [Fact]
    public async Task InterruptSoulImprintCancelsPreparedInterruptibleIntent()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 20);
        var (service, _) = CreateProductionSoulBattleService(test);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Room.RoundNumber = 4;
        test.Db.CharacterSoulImprints.Add(new CharacterSoulImprint
        {
            CharacterId = test.Character.Id, SoulImprintCode = "molten-warlord-brand",
            EquippedSlotIndex = SoulImprintRules.SlotIndex, AutoUseEnabled = true
        });
        test.Db.MonsterIntents.Add(new MonsterIntent
        {
            RoomId = test.Room.Id, RunSequence = test.Room.RunSequence, RoundNumber = test.Room.RoundNumber,
            MonsterId = test.Monster.Id, ActionType = "Skill", SkillCode = "goldtooth-smash",
            TargetType = "Front", TargetCharacterId = test.Character.Id, CreatedAtUtc = DateTime.UtcNow
        });
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("打断了") && log.Contains(test.Monster.Name));
        Assert.Equal(100, test.Character.Hp);
    }

    [Fact]
    public async Task BattleMilestonesExcludeCharacterWhoNeverFought()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        test.Db.AddRange(
            new User { Id = 2, UserName = "spectator", PasswordHash = "x", ActiveCharacterId = 2 },
            new Character { Id = 2, UserId = 2, Name = "Spectator", Hp = 0, MaxHp = 100, Attack = 10 },
            new RoomSlot { RoomId = test.Room.Id, SlotIndex = 2, UserId = 2, CharacterId = 2 });
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.True(result!.IsVictory);
        var milestones = await test.Db.CharacterBattleMilestones.ToListAsync();
        Assert.Equal(2, milestones.Count);
        Assert.All(milestones, item => Assert.Equal(test.Character.Id, item.CharacterId));
        Assert.Contains(milestones, item => item.Kind == BattleMilestoneService.MonsterKillKind && item.TargetCode == "slime-field");
        Assert.Contains(milestones, item => item.Kind == BattleMilestoneService.DungeonClearKind && item.TargetCode == "slime-field");
    }

    [Theory]
    [InlineData(ElementType.Fire, ElementType.Wind, 32, 91)]
    [InlineData(ElementType.Fire, ElementType.Water, 39, 85)]
    [InlineData(ElementType.Light, ElementType.Dark, 32, 91)]
    [InlineData(ElementType.Dark, ElementType.Light, 32, 91)]
    [InlineData(ElementType.Fire, ElementType.Fire, 35, 88)]
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
        Assert.Equal(88, test.Character.Hp);
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
        Assert.Equal(96, test.Character.Hp); // floor(12*0.75*0.5) = 4
        Assert.Contains(round.Logs, log => log.Contains("使用 盾击") && log.Contains("造成 16 点伤害"));
        Assert.Contains(round.Logs, log => log.Contains("使用 守护"));
    }

    [Fact]
    public async Task RetiredAttackTalentRankNoLongerModifiesCombat()
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Character.AttackTalentRank = 2;
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(35, result!.MonsterHp); // 旧属性加点字段不再参与战斗
        Assert.Equal(88, result.CharacterHp); // 基础减伤为 0%，承受完整的 12 点伤害
        Assert.Equal(20, test.Character.Attack);
    }

    [Fact]
    public async Task RepeatBattleRestoresHealthToTalentAdjustedMaximum()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 35, characterAttack: 100);
        test.Character.TalentMaxHpPercent = 10;
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
    public async Task SyncRoomAsync_RepeatBattleRespawnsAfterFiveSecondsAndKeepsManualCooldown()
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
        var beforeRespawn = await test.GetRoomDetailAsync();
        Assert.Equal(BattleRules.RepeatBattleDelaySeconds,
            (beforeRespawn!.NextBattleStartAtUtc!.Value - test.Room.BattleEndedAtUtc!.Value).TotalSeconds);

        test.Room.BattleEndedAtUtc = DateTime.UtcNow.AddSeconds(-BattleRules.RepeatBattleDelaySeconds);
        await test.Db.SaveChangesAsync();
        var (restarted, error) = await test.Service.SyncRoomAsync(1);

        Assert.Null(error);
        Assert.Equal(RoomStatus.Cooldown, restarted!.RoomStatus);
        Assert.Equal(BattleRules.RoundCooldownSeconds, test.Room.RoundCooldownDurationSeconds);
        Assert.InRange((restarted.NextRoundAvailableAtUtc!.Value - restarted.ServerTimeUtc).TotalSeconds, 4, 6);
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

        Assert.Null(result);
        Assert.Equal("RoomClosed", error);
        Assert.NotNull(test.Room.ClosedAtUtc);
        Assert.Null((await test.Db.RoomSlots.SingleAsync(slot => slot.RoomId == 1)).CharacterId);
        Assert.Equal(0, test.Character.Hp);
        Assert.True(test.Monster.Hp > 0);
        Assert.Equal(0, test.Character.Experience);
        Assert.Equal(1, (await test.GetRoomDetailAsync())!.CumulativeRewards!.CompletedRuns);
    }

    [Fact]
    public async Task SyncRoomAsync_RepeatBattleRespawnsBeforeAutoCooldownEnds()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 40, characterAttack: 100);
        test.Room.IsRepeatBattle = true;
        var mainSlot = await test.Db.RoomSlots.SingleAsync(slot => slot.RoomId == 1 && slot.IsMainControl);
        mainSlot.IsAutoEnabled = true;
        await test.Db.SaveChangesAsync();
        await test.Service.StartPreparationAsync(1, test.Token);
        test.Room.BattleEndedAtUtc = DateTime.UtcNow.AddSeconds(-BattleRules.RepeatBattleDelaySeconds);
        await test.Db.SaveChangesAsync();

        var (waiting, waitingError) = await test.Service.SyncRoomAsync(1);

        Assert.Null(waitingError);
        Assert.Equal(RoomStatus.Cooldown, waiting!.RoomStatus);
        Assert.Equal(test.Monster.MaxHp, test.Monster.Hp);
        Assert.Equal(BattleRules.AutoRoundCooldownSeconds, test.Room.RoundCooldownDurationSeconds);
        Assert.InRange((waiting.NextRoundAvailableAtUtc!.Value - waiting.ServerTimeUtc).TotalSeconds, 14, 16);
        Assert.Equal(0, test.Room.RoundNumber);

        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        var (result, error) = await test.Service.SyncRoomAsync(1);

        Assert.Null(error);
        Assert.Equal(RoomStatus.BattleOver, result!.RoomStatus);
        Assert.Equal(0, test.Monster.Hp);
        Assert.Equal(test.Character.MaxHp, test.Character.Hp);
        Assert.Contains(waiting.Logs, log => log.Contains("下一场副本战斗"));
        Assert.True(test.Room.BattleEndedAtUtc > DateTime.UtcNow.AddSeconds(-5));
        Assert.Equal(2, test.Character.Level);
        Assert.Equal(1, test.Character.TalentPoints);
        Assert.Equal(0, test.Character.Experience);
    }

    [Fact]
    public async Task RepeatBattleDoesNotStartNewRunWhenRoomExpiresAfterRespawn()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        test.Room.IsRepeatBattle = true;
        test.Room.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(1);
        (await test.Db.RoomSlots.SingleAsync()).IsAutoEnabled = true;
        await test.Db.SaveChangesAsync();
        await test.Service.StartPreparationAsync(test.Room.Id, test.Token);
        test.Room.BattleEndedAtUtc = DateTime.UtcNow.AddSeconds(-BattleRules.RepeatBattleDelaySeconds);
        await test.Db.SaveChangesAsync();

        var (waiting, waitError) = await test.Service.SyncRoomAsync(test.Room.Id);
        Assert.Null(waitError);
        Assert.Equal(RoomStatus.Cooldown, waiting!.RoomStatus);
        Assert.Equal(2, test.Room.RunSequence);

        test.Room.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        var (closed, closeError) = await test.Service.SyncRoomAsync(test.Room.Id);

        Assert.Null(closeError);
        Assert.Equal(RoomStatus.BattleOver, closed!.RoomStatus);
        Assert.NotNull(test.Room.ClosedAtUtc);
        Assert.Equal(0, test.Room.RoundNumber);
        Assert.Single(await test.Db.RewardRuns.ToListAsync());
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
        test.Db.CharacterBattleMilestones.RemoveRange(await test.Db.CharacterBattleMilestones.ToListAsync());
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.SetSlotAutoAsync(1, new Game.Shared.Dtos.SetSlotAutoRequest { SlotIndex = 1, IsAutoEnabled = true }, test.Token);

        Assert.Null(result);
        Assert.Equal("AutoNotUnlocked", error);
    }

    [Fact]
    public async Task SetSlotAutoAsync_OtherCharacterOnClearedAccount_IsRejected()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var newcomer = new Character { UserId = 1, Name = "Newcomer", Hp = 100, MaxHp = 100, Attack = 20 };
        test.Db.Characters.Add(newcomer);
        await test.Db.SaveChangesAsync();
        (await test.Db.Users.SingleAsync()).ActiveCharacterId = newcomer.Id;
        (await test.Db.RoomSlots.SingleAsync(slot => slot.RoomId == 1 && slot.IsMainControl)).CharacterId = newcomer.Id;
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.SetSlotAutoAsync(1,
            new Game.Shared.Dtos.SetSlotAutoRequest { SlotIndex = 1, IsAutoEnabled = true }, test.Token);

        Assert.Null(result);
        Assert.Equal("AutoNotUnlocked", error);
        Assert.True(await test.Db.UserDungeonClears.AnyAsync(clear => clear.UserId == 1 && clear.DungeonId == 1));
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
        Assert.Contains(victory.Logs, log => log.Contains("Mage 获得 10 点经验值"));
        var progression = ProgressionTestFactory.Create();
        var detail = await new RoomService(test.Db, new UserService(test.Db, progression, SkillTestFactory.Create()), progression, ConsumableTestFactory.Create(), SkillTestFactory.Create(), RewardTestFactory.CreateService(test.Db, progression)).GetRoomDetailAsync(1, test.Token);
        var mainSlot = detail!.Slots.Single(slot => slot.SlotIndex == 1);
        Assert.Equal(1, mainSlot.CharacterLevel);
        Assert.Equal(10, mainSlot.CharacterExperience);
        Assert.Equal(20, mainSlot.ExperienceToNextLevel);
        Assert.Equal(0, mainSlot.TalentPoints);
    }

    [Fact]
    public async Task VictoryExperienceUsesDungeonLevelDifferenceAndPersistsAdjustedReward()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        test.Character.Level = 3;
        await test.Db.SaveChangesAsync();

        var (victory, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(RoomStatus.BattleOver, victory!.RoomStatus);
        Assert.Equal(4, test.Character.Experience);
        Assert.Contains(victory.Logs, log => log.Contains("获得 4 点经验值"));
        Assert.Equal(4, await test.Db.RewardEntries.Where(entry => entry.Kind == "Experience").SumAsync(entry => entry.Quantity));
        var detail = await test.GetRoomDetailAsync();
        Assert.Equal(4, detail!.Rewards!.Experience);
    }

    [Fact]
    public async Task VictoryExperienceIsRemovedWhenDungeonIsFourLevelsBelowCharacter()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        test.Character.Level = 5;
        await test.Db.SaveChangesAsync();

        var (victory, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(RoomStatus.BattleOver, victory!.RoomStatus);
        Assert.Equal(0, test.Character.Experience);
        Assert.DoesNotContain(victory.Logs, log => log.Contains("点经验值"));
        Assert.Empty(await test.Db.RewardEntries.Where(entry => entry.Kind == "Experience").ToListAsync());
        var detail = await test.GetRoomDetailAsync();
        Assert.Equal(0, detail!.Rewards!.Experience);
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

        Assert.Equal(13, test.Character.Gold);
        Assert.Equal(13, (await test.Db.Characters.SingleAsync(item => item.UserId == 2)).Gold);
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
    public async Task GuaranteedEpicWeaponDropPersistsQualityCapacityAndDisplaysIt()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        var progression = ProgressionTestFactory.Create();
        var rewardService = RewardTestFactory.CreateService(test.Db, progression,
            guaranteedWeapon: true, qualityBonusLevels: 3);
        var service = new BattleService(test.Db,
            new UserService(test.Db, progression, SkillTestFactory.Create()), ConsumableTestFactory.Create(),
            SkillTestFactory.Create(), rewardService);

        var (victory, error) = await service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(RoomStatus.BattleOver, victory!.RoomStatus);
        var weapon = Assert.Single(await test.Db.CharacterWeapons.Include(item => item.Skills).ToListAsync());
        Assert.Equal(test.Character.Id, weapon.CharacterId);
        Assert.Equal("gale-bow", weapon.WeaponCode);
        Assert.Null(weapon.EquippedSlotIndex);
        Assert.Equal(7, weapon.Attack);
        var skill = Assert.Single(weapon.Skills);
        Assert.Equal(3, weapon.QualityRank);
        Assert.Equal((2, 2, 0, 0),
            (skill.Level, skill.BaseLevel, skill.QualityBonusLevel, skill.EnhancementLevel));
        Assert.Contains(victory.Logs, log => log.Contains("史诗·疾风短弓"));
        Assert.Contains(await test.Db.RewardEntries.ToListAsync(), entry =>
            entry.Kind == "Weapon" && entry.EventKey == "monster:1" && entry.WeaponSnapshotJson is not null);
        var roomService = new RoomService(test.Db,
            new UserService(test.Db, progression, SkillTestFactory.Create()), progression,
            ConsumableTestFactory.Create(), SkillTestFactory.Create(), rewardService);
        var detail = await roomService.GetRoomDetailAsync(test.Room.Id, test.Token);
        Assert.Equal("史诗·疾风短弓", detail!.Rewards!.Items.Single(item => item.Kind == "Weapon").Name);
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
        Assert.Equal(0, test.Character.Gold);

        var (defeat, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(RoomStatus.BattleOver, defeat!.RoomStatus);
        Assert.True(defeat.IsCharacterDead);
        Assert.Equal(3, test.Character.Gold);
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
        Assert.Equal(3, test.Character.Gold);
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
        Assert.Equal(3, test.Character.Gold);
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
        Assert.DoesNotContain(victory.Logs, log => log.Contains("使用 小型治疗药水"));
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
        Assert.Contains(first!.Logs, log => log.Contains("使用 小型治疗药水"));
        Assert.Equal(72, test.Character.Hp);
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
        Assert.Contains(round!.Logs, log => log.Contains("使用 小型治疗药水"));
        Assert.False((await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == test.Character.Id)).IsAutoEnabled);
        Assert.Equal(72, test.Character.Hp);
        Assert.Equal(0, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
    }

    [Fact]
    public async Task OperationPotionAppliesFromFirstRoundThroughTheSameRun()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 20, monsterAttack: 1);
        test.Monster.Hp = test.Monster.MaxHp = 500;
        await test.AddOperationPotionAsync(test.Character, quantity: 2);

        var (first, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(482, first!.MonsterHp); // floor(20 * 1.15 - 5)
        Assert.Contains(first.Logs, log => log.Contains("使用 北郡战意药剂"));
        Assert.Equal(1, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
        var firstSlot = (await test.GetRoomDetailAsync())!.Slots.Single(slot => slot.CharacterId == test.Character.Id);
        var effect = firstSlot.OperationPotion;
        Assert.True(effect!.IsActive);
        Assert.Equal(15, effect.AttackPercent);
        var buff = Assert.Single(firstSlot.StatusEffects, status => status.Code == "operation-potion:northshire-battle-draught");
        Assert.True(buff.IsPositive);
        Assert.True(buff.ExpiresWithRun);
        Assert.False(buff.CanDispel);
        Assert.Contains("15%", buff.Description);

        await test.CompleteCooldownAndPrepareAsync();

        Assert.Equal(464, test.Monster.Hp);
        Assert.Equal(1, (await test.Db.CharacterItemStacks.SingleAsync(item => item.ItemCode == "northshire-battle-draught")).Quantity);
        Assert.Single(await test.Db.BattleOperationPotionStates.ToListAsync());
        Assert.Contains((await test.GetRoomDetailAsync())!.Slots.Single().StatusEffects,
            status => status.Code == "operation-potion:northshire-battle-draught");
    }

    [Fact]
    public async Task OperationPotionAlsoBoostsDamageSkillsInTheFirstRound()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 20, monsterAttack: 1);
        test.Monster.Hp = test.Monster.MaxHp = 500;
        await test.AddOperationPotionAsync(test.Character, quantity: 1);
        await test.AddSkillAsync(test.Character, 1, "knight-strike", autoUse: true);

        var (round, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Contains(round!.Logs, log => log.Contains("普通攻击") && log.Contains("造成 18 点伤害"));
        Assert.Contains(round.Logs, log => log.Contains("使用 盾击") && log.Contains("造成 27 点伤害"));
        Assert.Equal(455, round.MonsterHp);
    }

    [Fact]
    public async Task RepeatBattleConsumesOperationPotionAgainAfterNewRunStarts()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1);
        test.Room.IsRepeatBattle = true;
        (await test.Db.RoomSlots.SingleAsync(slot => slot.RoomId == 1 && slot.IsMainControl)).IsAutoEnabled = true;
        await test.AddOperationPotionAsync(test.Character, quantity: 2);

        var (first, error) = await test.Service.StartPreparationAsync(1, test.Token);
        Assert.Null(error);
        Assert.Equal(RoomStatus.BattleOver, first!.RoomStatus);
        Assert.Equal(1, (await test.Db.CharacterItemStacks.SingleAsync(item => item.ItemCode == "northshire-battle-draught")).Quantity);
        Assert.DoesNotContain((await test.GetRoomDetailAsync())!.Slots.Single().StatusEffects,
            status => status.Code.StartsWith("operation-potion:"));
        test.Room.BattleEndedAtUtc = DateTime.UtcNow.AddSeconds(-31);
        await test.Db.SaveChangesAsync();

        var (second, restartError) = await test.Service.SyncRoomAsync(1);

        Assert.Null(restartError);
        Assert.Equal(RoomStatus.BattleOver, second!.RoomStatus);
        Assert.Equal(2, test.Room.RunSequence);
        Assert.Equal(0, (await test.Db.CharacterItemStacks.SingleAsync(item => item.ItemCode == "northshire-battle-draught")).Quantity);
        var state = Assert.Single(await test.Db.BattleOperationPotionStates.ToListAsync());
        Assert.Equal(2, state.RunSequence);
        Assert.Equal(15, state.AttackPercent);
        Assert.DoesNotContain((await test.GetRoomDetailAsync())!.Slots.Single().StatusEffects,
            status => status.Code.StartsWith("operation-potion:"));
    }

    [Fact]
    public async Task ResetBattleClearsOperationPotionBeforeTheNextRun()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        await test.AddOperationPotionAsync(test.Character, quantity: 1);
        var (victory, error) = await test.Service.StartPreparationAsync(1, test.Token);
        Assert.Null(error);
        Assert.Equal(RoomStatus.BattleOver, victory!.RoomStatus);
        Assert.Single(await test.Db.BattleOperationPotionStates.ToListAsync());

        var (reset, resetError) = await test.Service.ResetBattleAsync(1, test.Token);

        Assert.True(reset);
        Assert.Null(resetError);
        Assert.Empty(await test.Db.BattleOperationPotionStates.ToListAsync());
        Assert.DoesNotContain((await test.GetRoomDetailAsync())!.Slots.Single().StatusEffects,
            status => status.Code.StartsWith("operation-potion:"));
    }

    [Fact]
    public async Task OperationPotionWithoutStockIsSkippedForTheWholeRun()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 20, monsterAttack: 1);
        test.Monster.Hp = test.Monster.MaxHp = 500;
        await test.AddOperationPotionAsync(test.Character, quantity: 0);

        var (first, error) = await test.Service.StartPreparationAsync(1, test.Token);
        Assert.Null(error);
        Assert.Equal(485, first!.MonsterHp);
        Assert.Contains(first.Logs, log => log.Contains("库存不足"));
        var stock = await test.Db.CharacterItemStacks.SingleAsync();
        stock.Quantity = 1;
        stock.Version++;
        await test.Db.SaveChangesAsync();

        await test.CompleteCooldownAndPrepareAsync();

        Assert.Equal(470, test.Monster.Hp);
        Assert.Equal(1, stock.Quantity);
        Assert.Equal(0, (await test.Db.BattleOperationPotionStates.SingleAsync()).AttackPercent);
        Assert.DoesNotContain((await test.GetRoomDetailAsync())!.Slots.Single().StatusEffects,
            status => status.Code.StartsWith("operation-potion:"));
    }

    [Fact]
    public async Task CharacterJoiningBetweenRoundsUsesOperationPotionOnItsFirstRound()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 20, monsterAttack: 1);
        test.Monster.Hp = test.Monster.MaxHp = 500;
        var guest = new Character { Id = 2, UserId = 2, Name = "Guest", Hp = 100, MaxHp = 100, Attack = 20 };
        test.Db.AddRange(
            new User { Id = 2, UserName = "guest", PasswordHash = "x", ActiveCharacterId = 2 },
            guest,
            new RoomSlot { RoomId = 1, SlotIndex = 2 },
            new UserLoginSession { UserId = 2, Token = "other-token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) },
            new CharacterItemStack { CharacterId = 2, ItemCode = "northshire-battle-draught", Quantity = 1 },
            new CharacterConsumableSlot { CharacterId = 2, SlotIndex = 3, ItemCode = "northshire-battle-draught" });
        await test.Db.SaveChangesAsync();

        await test.Service.StartPreparationAsync(1, test.Token);
        Assert.Equal(RoomStatus.Cooldown, test.Room.Status);
        var progression = ProgressionTestFactory.Create();
        var roomService = new RoomService(test.Db,
            new UserService(test.Db, progression, SkillTestFactory.Create()), progression,
            ConsumableTestFactory.Create(), SkillTestFactory.Create(), RewardTestFactory.CreateService(test.Db, progression));

        var (joined, joinError) = await roomService.JoinRoomAsync(1,
            new Game.Shared.Dtos.JoinRoomRequest { SlotIndex = 2 }, "other-token");
        Assert.Null(joinError);
        Assert.Equal(RoomStatus.Cooldown, joined!.RoomStatus);
        Assert.Equal(guest.Id, joined!.Slots.Single(slot => slot.SlotIndex == 2).CharacterId);
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        await test.Service.SyncRoomAsync(1);
        Assert.Equal(RoomStatus.NotStarted, test.Room.Status);
        await test.Service.StartPreparationAsync(1, test.Token);
        var (round, roundError) = await test.Service.StartPreparationAsync(1, "other-token");

        Assert.Null(roundError);
        Assert.Contains(round!.Logs, log => log.Contains("Guest 使用 北郡战意药剂"));
        Assert.Equal(0, (await test.Db.CharacterItemStacks.SingleAsync(item => item.CharacterId == 2)).Quantity);
        Assert.Equal(15, (await test.Db.BattleOperationPotionStates.SingleAsync(state => state.CharacterId == 2)).AttackPercent);
        var guestStatus = (await roomService.GetRoomDetailAsync(1, test.Token))!.Slots.Single(slot => slot.CharacterId == guest.Id).StatusEffects;
        Assert.Contains(guestStatus, status => status.Code == "operation-potion:northshire-battle-draught");
    }

    [Fact]
    public async Task CharacterJoiningBetweenRepeatRunsUsesOperationPotionInTheNextRun()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        test.Room.IsRepeatBattle = true;
        var guest = new Character { Id = 2, UserId = 2, Name = "Guest", Hp = 100, MaxHp = 100, Attack = 20 };
        test.Db.AddRange(
            new User { Id = 2, UserName = "guest", PasswordHash = "x", ActiveCharacterId = 2 },
            guest,
            new RoomSlot { RoomId = 1, SlotIndex = 2 },
            new UserLoginSession { UserId = 2, Token = "other-token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
        await test.Db.SaveChangesAsync();
        await test.AddOperationPotionAsync(guest, quantity: 1);
        var (first, firstError) = await test.Service.StartPreparationAsync(1, test.Token);
        Assert.Null(firstError);
        Assert.Equal(RoomStatus.BattleOver, first!.RoomStatus);

        var progression = ProgressionTestFactory.Create();
        var roomService = new RoomService(test.Db,
            new UserService(test.Db, progression, SkillTestFactory.Create()), progression,
            ConsumableTestFactory.Create(), SkillTestFactory.Create(), RewardTestFactory.CreateService(test.Db, progression));
        var (joined, joinError) = await roomService.JoinRoomAsync(1,
            new Game.Shared.Dtos.JoinRoomRequest { SlotIndex = 2 }, "other-token");
        Assert.Null(joinError);
        Assert.Equal(RoomStatus.BattleOver, joined!.RoomStatus);

        test.Room.BattleEndedAtUtc = DateTime.UtcNow.AddSeconds(-31);
        await test.Db.SaveChangesAsync();
        await test.Service.SyncRoomAsync(1);
        await test.Service.StartPreparationAsync(1, test.Token);
        var (second, secondError) = await test.Service.StartPreparationAsync(1, "other-token");

        Assert.Null(secondError);
        Assert.Contains(second!.Logs, log => log.Contains("Guest 使用 北郡战意药剂"));
        Assert.Equal(0, (await test.Db.CharacterItemStacks.SingleAsync(stack =>
            stack.CharacterId == guest.Id && stack.ItemCode == "northshire-battle-draught")).Quantity);
        Assert.Equal(2, (await test.Db.BattleOperationPotionStates.SingleAsync(state =>
            state.CharacterId == guest.Id)).RunSequence);
    }

    [Fact]
    public async Task OperationPotionCannotBeEquippedOrQueuedAsOrdinaryConsumable()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var consumables = test.CreateConsumableService();
        var wrongOrdinary = await consumables.SetSlotAsync(test.Token, test.Character.Id, 1,
            new Game.Shared.Dtos.Characters.SetConsumableSlotRequest { ItemCode = "northshire-battle-draught" });
        var wrongOperation = await consumables.SetSlotAsync(test.Token, test.Character.Id, 3,
            new Game.Shared.Dtos.Characters.SetConsumableSlotRequest { ItemCode = "minor-healing-potion" });
        var correct = await consumables.SetSlotAsync(test.Token, test.Character.Id, 3,
            new Game.Shared.Dtos.Characters.SetConsumableSlotRequest { ItemCode = "northshire-battle-draught" });
        var queued = await test.Service.QueueConsumableAsync(new Game.Shared.Dtos.QueueConsumableRequest
        {
            RoomId = 1, CharacterId = test.Character.Id, ConsumableSlotIndex = 3
        }, test.Token);

        Assert.Equal("WrongConsumableSlot", wrongOrdinary.Error);
        Assert.Equal("WrongConsumableSlot", wrongOperation.Error);
        Assert.Null(correct.Error);
        Assert.Equal("northshire-battle-draught", correct.Response!.Slots.Single(slot => slot.SlotIndex == 3).ItemCode);
        Assert.Equal("InvalidSlotIndex", queued.Error);
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
        Assert.Contains(round!.Logs, log => log.Contains("使用 治疗术"));
        Assert.Equal(remainingPotions == 0, round.Logs.Any(log => log.Contains("使用 小型治疗药水")));
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
        Assert.Equal(2, round.Logs.Count(log => log.Contains("使用 小型治疗药水")));
        Assert.Equal(72, test.Character.Hp);
        Assert.Equal(60, guest.Hp);
        Assert.All(await test.Db.CharacterItemStacks.ToListAsync(), stack => Assert.Equal(0, stack.Quantity));
        Assert.Equal(2, await test.Db.BattleConsumableCooldowns.CountAsync());
    }

    [Fact]
    public async Task ManualSkillsFireBeforeNormalAttackAndMonsterCounterattack()
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
        Assert.Equal(54, test.Character.Hp);
        var strike = round.Logs.FindIndex(log => log.Contains("使用 盾击"));
        var guard = round.Logs.FindIndex(log => log.Contains("使用 守护"));
        var normalAttack = round.Logs.FindIndex(log => log.Contains("Knight 普通攻击"));
        var counterattack = round.Logs.FindIndex(log => log.Contains("Slime 普通攻击"));
        Assert.True(strike >= 0 && guard > strike && normalAttack > guard && counterattack > normalAttack);
        Assert.Equal(new[] { 3, 4 }, (await test.Db.BattleSkillCooldowns.OrderBy(entry => entry.SkillCode).ToListAsync())
            .Select(entry => entry.ReadyAtRound).OrderBy(round => round));
        Assert.Equal(0, (await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == 1)).PendingSkillSlotMask);
    }

    [Fact]
    public async Task SkillDebuffIncreasesNormalAttackDamageInTheSameRound()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 20, monsterDefense: 0);
        test.Character.ProfessionCode = "knight";
        test.Monster.Hp = test.Monster.MaxHp = 200;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "knight-break", autoUse: true);
        var progression = ProgressionTestFactory.Create();
        var rewards = RewardTestFactory.CreateService(test.Db, progression);
        var monsterCombat = new MonsterCombatService(test.Db, MonsterCombatTestFactory.CreateCatalog());
        var skills = SkillTestFactory.CreateResponses();
        var service = new BattleService(test.Db, new UserService(test.Db, progression, skills),
            ConsumableTestFactory.Create(), skills, rewards,
            new DungeonRunService(test.Db, rewards, monsterCombat), monsterCombat);

        var (round, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.NotNull(round);
        var skillIndex = round.Logs.FindIndex(log => log.Contains("使用 破甲斩 攻击"));
        var debuffIndex = round.Logs.FindIndex(log => log.Contains("获得 破甲"));
        var normalIndex = round.Logs.FindIndex(log => log.Contains("Knight 普通攻击"));
        Assert.True(skillIndex >= 0 && debuffIndex > skillIndex && normalIndex > debuffIndex);
        Assert.Contains("造成 24 点伤害", round.Logs[normalIndex]);
        Assert.Equal("armor-break", (await test.Db.BattleStatusEffects.SingleAsync()).EffectCode);
    }

    [Fact]
    public async Task PreviousRoundTalentEchoAppliesAndSkillRefreshesItForTheNextRound()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 20, monsterAttack: 1, monsterDefense: 0);
        test.Character.Level = 2;
        test.Monster.Hp = test.Monster.MaxHp = 500;
        test.Db.CharacterSkillTalents.Add(new CharacterSkillTalent
            { CharacterId = test.Character.Id, NodeCode = "sword-rhythm", PointsSpent = 1 });
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "sword-slash", autoUse: true);
        var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();
        var skills = new SkillCatalog(Options.Create(config.GetSection(SkillOptions.SectionName).Get<SkillOptions>()!));
        var progression = ProgressionTestFactory.Create();
        var service = new BattleService(test.Db, new UserService(test.Db, progression, skills),
            ConsumableTestFactory.Create(), skills, RewardTestFactory.CreateService(test.Db, progression));

        var (first, firstError) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(firstError);
        Assert.DoesNotContain(first!.Logs, log => log.Contains("普攻追击"));
        Assert.Equal(0, (await test.Db.BattleStatusEffects.SingleAsync()).AppliedRound);

        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        (await test.Db.BattleSkillCooldowns.SingleAsync()).ReadyAtRound = test.Room.RoundNumber;
        await test.Db.SaveChangesAsync();
        var (second, secondError) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(secondError);
        Assert.Contains(second!.Logs, log => log.Contains("普攻追击"));
        Assert.Equal(1, (await test.Db.BattleStatusEffects.SingleAsync()).AppliedRound);
    }

    [Fact]
    public async Task AutoSkillsSkipUnsatisfiedConditionAndContinueThroughFiveSlots()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1);
        await test.AddSkillAsync(test.Character, 1, "knight-guard", autoUse: true, threshold: 70);
        await test.AddSkillAsync(test.Character, 5, "knight-strike", autoUse: true);

        var (round, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Contains(round!.Logs, log => log.Contains("使用 盾击"));
        Assert.DoesNotContain(round.Logs, log => log.Contains("使用 守护"));
        Assert.Single(await test.Db.BattleSkillCooldowns.ToListAsync());
    }

    [Fact]
    public async Task AutoGuardSkillsAvoidSpendingMultipleCooldownsOnTheSameTarget()
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
        Assert.Single(round!.Logs, log => log.Contains("使用 守护"));
        Assert.Single(await test.Db.BattleSkillCooldowns.ToListAsync());
        Assert.DoesNotContain(await test.Db.BattleSkillCooldowns.ToListAsync(), cooldown => cooldown.CharacterId == second.Id);
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
        var strike = round!.Logs.FindIndex(log => log.Contains("使用 盾击"));
        var guard = round.Logs.FindIndex(log => log.Contains("使用 守护"));
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
        Assert.Contains(round!.Logs, log => log.Contains("使用 治疗术，为 1号位"));
        Assert.Contains(round.Logs, log => log.Contains("使用 圣光击"));
        Assert.Equal(68, test.Character.Hp);
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
        var heal = round!.Logs.FindIndex(log => log.Contains("使用 治疗术"));
        var strike = round.Logs.FindIndex(log => log.Contains("使用 盾击"));
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
        Assert.DoesNotContain(firstRound!.Logs, log => log.Contains("使用 破甲斩"));

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
        Assert.Contains(secondRound!.Logs, log => log.Contains("使用 破甲斩"));
    }

    [Fact]
    public async Task ManualUseCanOverrideAutomaticHpThreshold()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 60, characterAttack: 1, monsterAttack: 5);
        await test.AddPotionAsync(test.Character, quantity: 1, autoUse: true, threshold: 50);

        await test.Service.StartPreparationAsync(1, test.Token);
        Assert.Equal(55, test.Character.Hp);
        Assert.Equal(1, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);

        var (queued, queueError) = await test.Service.QueueConsumableAsync(
            new Game.Shared.Dtos.QueueConsumableRequest { RoomId = 1, CharacterId = test.Character.Id, ConsumableSlotIndex = 1 }, test.Token);
        Assert.True(queued);
        Assert.Null(queueError);
        await test.CompleteCooldownAndPrepareAsync();

        Assert.Equal(70, test.Character.Hp);
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
        Assert.Equal(26, (await verificationDb.Characters.SingleAsync()).Gold);
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
        Assert.Equal(0, secondLoadout!.Items.Single(item => item.Code == "minor-healing-potion").Quantity);
        Assert.Equal(3, firstLoadout!.Items.Single(item => item.Code == "minor-healing-potion").Quantity);
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
        Assert.Equal(88, result!.CharacterHp);
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
        Assert.Contains(result.Logs, log => log.Contains("临时切换为自动战斗"));
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
    public async Task SyncAsync_AllMembersAuto_StartsRoundAndUsesTwentySecondCooldown()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1, characterDefense: 99);
        var otherSlot = await test.AddOtherMemberAsync();
        (await test.Db.RoomSlots.SingleAsync(slot => slot.RoomId == 1 && slot.SlotIndex == 1)).IsAutoEnabled = true;
        otherSlot.IsAutoEnabled = true;
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.SyncAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(RoomStatus.Cooldown, result!.RoomStatus);
        Assert.InRange((result.NextRoundAvailableAtUtc!.Value - result.ServerTimeUtc).TotalSeconds, 19, 21);
    }

    [Fact]
    public async Task OfflinePartyWithEnabledAutoAdvancesAndDisablingResumesManualTiming()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        await test.AddOtherMemberAsync();
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Room.StartedAtUtc = DateTime.UtcNow.AddMinutes(-2);
        var slots = await test.Db.RoomSlots.Where(slot => slot.RoomId == 1).ToListAsync();
        foreach (var slot in slots)
        {
            slot.LastSeenAtUtc = DateTime.UtcNow.AddMinutes(-2);
            slot.IsAutoEnabled = true;
        }
        await test.Db.SaveChangesAsync();

        var (autoRound, autoError) = await test.Service.SyncRoomAsync(1);

        Assert.Null(autoError);
        Assert.Equal(1, test.Room.RoundNumber);
        Assert.Equal(RoomStatus.Cooldown, autoRound!.RoomStatus);
        Assert.Equal(BattleRules.AutoRoundCooldownSeconds, test.Room.RoundCooldownDurationSeconds);
        Assert.All(slots, slot => Assert.True(slot.IsAutoEnabled));
        Assert.Contains(autoRound.Logs, log => log.Contains("1号位") && log.Contains("普通攻击"));
        Assert.Contains(autoRound.Logs, log => log.Contains("2号位") && log.Contains("普通攻击"));

        var (returned, returnError) = await test.Service.SetSlotAutoAsync(1,
            new Game.Shared.Dtos.SetSlotAutoRequest { SlotIndex = 1, IsAutoEnabled = false }, test.Token);
        Assert.Null(returnError);
        Assert.False(slots[0].IsAutoEnabled);
        Assert.True(slots[1].IsAutoEnabled);
        Assert.Equal(BattleRules.RoundCooldownSeconds, test.Room.RoundCooldownDurationSeconds);
        Assert.InRange((returned!.NextRoundAvailableAtUtc!.Value - returned.ServerTimeUtc).TotalSeconds, 9, 11);
        var detail = await test.GetRoomDetailAsync();
        Assert.False(detail!.Slots.Single(slot => slot.SlotIndex == 1).IsOfflineAuto);
        Assert.True(detail.Slots.Single(slot => slot.SlotIndex == 2).IsOfflineAuto);

        var (queued, queueError) = await test.Service.StartPreparationAsync(1, test.Token);
        Assert.Null(queueError);
        Assert.Equal(1, test.Room.RoundNumber);
        Assert.True(slots.All(slot => slot.IsConfirmed));
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        var (manualRound, manualError) = await test.Service.SyncRoomAsync(1);
        Assert.Null(manualError);
        Assert.Equal(2, test.Room.RoundNumber);
        Assert.Equal(RoomStatus.Cooldown, manualRound!.RoomStatus);
    }

    [Fact]
    public async Task OfflineManualCharacterUsesTimeoutAndManualCooldownWithoutAutoUnlock()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        test.Room.StartedAtUtc = DateTime.UtcNow.AddMinutes(-2);
        test.Room.PreparationStartedAtUtc = DateTime.UtcNow.AddSeconds(-31);
        (await test.Db.RoomSlots.SingleAsync()).LastSeenAtUtc = DateTime.UtcNow.AddMinutes(-2);
        test.Db.CharacterBattleMilestones.RemoveRange(await test.Db.CharacterBattleMilestones.ToListAsync());
        await test.Db.SaveChangesAsync();

        var (round, error) = await test.Service.SyncRoomAsync(1);

        Assert.Null(error);
        Assert.Equal(RoomStatus.Cooldown, round!.RoomStatus);
        Assert.Equal(BattleRules.RoundCooldownSeconds, test.Room.RoundCooldownDurationSeconds);
        Assert.Contains(round.Logs, log => log.Contains("准备超时"));
        var detail = await test.GetRoomDetailAsync();
        Assert.True(detail!.Slots[0].IsOffline);
        Assert.False(detail.Slots[0].IsOfflineAuto);
    }

    [Fact]
    public async Task PreparingRoomContinuesWhenLastOnlineMemberGoesOffline()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        await test.AddOtherMemberAsync();
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Room.Status = RoomStatus.Preparing;
        test.Room.StartedAtUtc = DateTime.UtcNow.AddMinutes(-2);
        test.Room.IsPreparationTimeoutEnabled = false;
        var slots = await test.Db.RoomSlots.Where(slot => slot.RoomId == 1).ToListAsync();
        slots.Single(slot => slot.SlotIndex == 1).IsConfirmed = true;
        foreach (var slot in slots)
        {
            slot.LastSeenAtUtc = DateTime.UtcNow.AddMinutes(-2);
            slot.IsAutoEnabled = true;
        }
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.SyncRoomAsync(1);

        Assert.Null(error);
        Assert.Equal(1, test.Room.RoundNumber);
        Assert.Equal(RoomStatus.Cooldown, result!.RoomStatus);
        Assert.Equal(BattleRules.AutoRoundCooldownSeconds, test.Room.RoundCooldownDurationSeconds);
        Assert.All(slots, slot => Assert.False(slot.IsConfirmed));
    }

    [Fact]
    public async Task OfflineMultiplayerRepeatBattleRestartsAndSettlesBothRuns()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        await test.AddOtherMemberAsync();
        test.Room.IsRepeatBattle = true;
        test.Room.StartedAtUtc = DateTime.UtcNow.AddMinutes(-2);
        test.Room.ExpiresAtUtc = DateTime.UtcNow.AddHours(1);
        test.Room.IsPreparationTimeoutEnabled = false;
        test.Monster.Hp = test.Monster.MaxHp = 10;
        foreach (var slot in await test.Db.RoomSlots.Where(slot => slot.RoomId == 1).ToListAsync())
        {
            slot.LastSeenAtUtc = DateTime.UtcNow.AddMinutes(-2);
            slot.IsAutoEnabled = true;
        }
        await test.Db.SaveChangesAsync();

        var (first, firstError) = await test.Service.SyncRoomAsync(1);
        Assert.Null(firstError);
        Assert.Equal(RoomStatus.BattleOver, first!.RoomStatus);
        Assert.Equal(1, test.Room.RunSequence);
        Assert.Equal(2, await test.Db.RewardEntries.Where(entry => entry.Sequence == 1 && entry.Kind == "Gold")
            .Select(entry => entry.CharacterId).Distinct().CountAsync());

        test.Room.BattleEndedAtUtc = DateTime.UtcNow.AddSeconds(-BattleRules.RepeatBattleDelaySeconds - 1);
        await test.Db.SaveChangesAsync();
        var (second, secondError) = await test.Service.SyncRoomAsync(1);
        Assert.Null(secondError);
        Assert.Equal(RoomStatus.Cooldown, second!.RoomStatus);
        Assert.Equal(2, test.Room.RunSequence);
        Assert.InRange((second.NextRoundAvailableAtUtc!.Value - second.ServerTimeUtc).TotalSeconds, 13, 15);
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        var (third, thirdError) = await test.Service.SyncRoomAsync(1);
        Assert.Null(thirdError);
        Assert.Equal(RoomStatus.BattleOver, third!.RoomStatus);
        Assert.Equal(2, await test.Db.RewardEntries.Where(entry => entry.Sequence == 2 && entry.Kind == "Gold")
            .Select(entry => entry.CharacterId).Distinct().CountAsync());
        Assert.Equal(2, await test.Db.RewardRuns.CountAsync(run => run.Status == "Victory"));
    }

    [Fact]
    public async Task GuestCanJoinAfterFirstRoundAndShareTheFollowingVictory()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        var (first, firstError) = await test.Service.StartPreparationAsync(1, test.Token);
        Assert.Null(firstError);
        Assert.Equal(RoomStatus.Cooldown, first!.RoomStatus);
        Assert.Equal(1, test.Room.RoundNumber);

        var guest = new Character { Id = 2, UserId = 2, Name = "Guest", Hp = 25, MaxHp = 100, Attack = 100 };
        test.Db.AddRange(new User { Id = 2, UserName = "guest", PasswordHash = "x", ActiveCharacterId = 2 },
            guest, new RoomSlot { RoomId = 1, SlotIndex = 2 },
            new UserLoginSession { UserId = 2, Token = "guest-token",
                CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
        await test.Db.SaveChangesAsync();
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        var rooms = new RoomService(test.Db, new UserService(test.Db, progression, skills), progression,
            ConsumableTestFactory.Create(), skills, RewardTestFactory.CreateService(test.Db, progression));

        var (joined, joinError) = await rooms.JoinRoomAsync(1,
            new JoinRoomRequest { SlotIndex = 2 }, "guest-token");
        Assert.Null(joinError);
        Assert.Equal(100, guest.Hp);
        Assert.False(joined!.Slots.Single(slot => slot.SlotIndex == 2).IsConfirmed);

        Assert.Null((await test.Service.StartPreparationAsync(1, test.Token)).Error);
        Assert.Null((await test.Service.StartPreparationAsync(1, "guest-token")).Error);
        Assert.Equal(1, test.Room.RoundNumber);
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        var (victory, victoryError) = await test.Service.SyncRoomAsync(1);

        Assert.Null(victoryError);
        Assert.Equal(RoomStatus.BattleOver, victory!.RoomStatus);
        Assert.Equal(2, test.Room.RoundNumber);
        Assert.Equal(2, await test.Db.RewardEntries.Where(entry => entry.Sequence == 1 && entry.Kind == "Gold")
            .Select(entry => entry.CharacterId).Distinct().CountAsync());
        Assert.True((await test.Db.RoomSlots.SingleAsync(slot => slot.RoomId == 1 && slot.SlotIndex == 2)).HasParticipatedInRun);
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
        Assert.Equal(88, test.Character.Hp);
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
        Assert.Equal(88, test.Character.Hp);
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
    public async Task StartPreparationAsync_DuringCooldown_QueuesWithoutResolvingRound()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var (firstResult, _) = await test.Service.StartPreparationAsync(1, test.Token);
        var beforeQueue = await test.GetRoomDetailAsync();
        var (result, error) = await test.Service.StartPreparationAsync(1, test.Token);
        var slot = await test.Db.RoomSlots.SingleAsync(x => x.RoomId == 1 && x.SlotIndex == 1);
        var afterQueue = await test.GetRoomDetailAsync();

        Assert.Null(error);
        Assert.True(beforeQueue!.CanPrepare);
        Assert.Equal(firstResult!.CharacterHp, result!.CharacterHp);
        Assert.Equal(firstResult.MonsterHp, result.MonsterHp);
        Assert.Equal(firstResult.RoomStatus, result.RoomStatus);
        Assert.True(slot.IsConfirmed);
        Assert.False(afterQueue!.CanPrepare);
    }

    [Fact]
    public async Task SyncAsync_QueuedPreparation_ResolvesAsSoonAsCooldownExpires()
    {
        await using var test = await BattleTestContext.CreateAsync();
        await test.Service.StartPreparationAsync(1, test.Token);
        var (queued, queueError) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(queueError);
        Assert.Equal(RoomStatus.Cooldown, queued!.RoomStatus);
        Assert.Equal(1, test.Room.RoundNumber);
        Assert.Equal(35, test.Monster.Hp);

        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        test.Room.Version++;
        await test.Db.SaveChangesAsync();
        var (result, error) = await test.Service.SyncAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(RoomStatus.Cooldown, result!.RoomStatus);
        Assert.Equal(2, test.Room.RoundNumber);
        Assert.Equal(20, result.MonsterHp);
        Assert.Contains(result.Logs, log => log.Contains("已准备的操作开始结算"));
        Assert.All(await test.Db.RoomSlots.Where(slot => slot.RoomId == 1).ToListAsync(), slot => Assert.False(slot.IsConfirmed));
    }

    [Fact]
    public async Task StartPreparationAsync_StaleRoundRequest_DoesNotQueueNextRound()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var (first, firstError) = await test.Service.StartPreparationAsync(1, test.Token, 0);
        var (stale, staleError) = await test.Service.StartPreparationAsync(1, test.Token, 0);
        var slot = await test.Db.RoomSlots.SingleAsync(x => x.RoomId == 1 && x.SlotIndex == 1);

        Assert.Null(firstError);
        Assert.Equal(RoomStatus.Cooldown, first!.RoomStatus);
        Assert.Equal(1, test.Room.RoundNumber);
        Assert.Equal("StaleRound", staleError);
        Assert.Equal(RoomStatus.Cooldown, stale!.RoomStatus);
        Assert.False(slot.IsConfirmed);
        Assert.Equal(35, test.Monster.Hp);
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

    [Fact]
    public async Task ResetBattleAsync_AfterDefeat_RestoresPartyForRetry()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 5, characterAttack: 1, monsterAttack: 100);
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

    [Fact]
    public async Task RepeatBattleExpiresAfterCurrentRunAndReleasesCharacter()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        test.Room.IsRepeatBattle = true;
        test.Room.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
        test.Room.Status = RoomStatus.Preparing;
        test.Db.CharacterActivities.Add(new CharacterActivity
        {
            CharacterId = test.Character.Id, Kind = CharacterActivityManager.BattleKind,
            SourceId = test.Room.Id, StartedAtUtc = DateTime.UtcNow.AddHours(-12), EndsAtUtc = test.Room.ExpiresAtUtc
        });
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(RoomStatus.BattleOver, result!.RoomStatus);
        Assert.NotNull(test.Room.ClosedAtUtc);
        Assert.Null((await test.Db.RoomSlots.SingleAsync(slot => slot.RoomId == 1)).CharacterId);
        Assert.False(await test.Db.CharacterActivities.AnyAsync());
        var detail = await test.GetRoomDetailAsync();
        Assert.NotNull(detail!.CumulativeRewards);
        Assert.True(detail.CumulativeRewards.CompletedRuns >= 1);
    }

    [Fact]
    public async Task ExpiredRepeatBattleDoesNotStartAnotherRun()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        test.Room.IsRepeatBattle = true;
        await test.Db.SaveChangesAsync();
        await test.Service.StartPreparationAsync(1, test.Token);
        Assert.Equal(RoomStatus.BattleOver, test.Room.Status);
        test.Room.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
        test.Room.BattleEndedAtUtc = DateTime.UtcNow.AddSeconds(-31);
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.SyncRoomAsync(1);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(RoomStatus.BattleOver, test.Room.Status);
        Assert.Equal(1, test.Room.RunSequence);
        Assert.NotNull(test.Room.ClosedAtUtc);
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
        test.Db.Characters.Add(new Character { Id = 2, UserId = 2, Name = "Other", Hp = 100, MaxHp = 100, Attack = 20});
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

        var results = await Task.WhenAll(firstService.StartPreparationAsync(1, test.Token, 0), secondService.StartPreparationAsync(1, test.Token, 0));
        Assert.Single(results, result => result.Error is null);
        await using var verificationDb = test.CreateDbContext();
        Assert.Equal(0, (await verificationDb.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Equal(68, (await verificationDb.Characters.SingleAsync()).Hp);
    }

    [Fact]
    public async Task ExecuteRoundAsync_UsesSlotOrderForPartyAttacks()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1, characterDefense: 99);
        await test.AddSlotAsync(2, "Mage", attack: 10);

        var (result, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.StartsWith("1号位 Knight 普通攻击", result!.Logs[0]);
        Assert.StartsWith("2号位 Mage 普通攻击", result.Logs[1]);
    }

    [Fact]
    public async Task ExecuteRoundAsync_StopsLaterSlotsWhenMonsterDies()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100);
        await test.AddSlotAsync(2, "Mage", attack: 100);

        var (result, error) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Single(result!.Logs, x => x.Contains("普通攻击 Slime"));
        Assert.DoesNotContain(result.Logs, x => x.Contains("2号位 Mage 普通攻击"));
        Assert.DoesNotContain(result.Logs, x => x.Contains("Slime 普通攻击"));
    }

    [Fact]
    public async Task MultiWaveBattle_KillAdvancesEnemyAndSettlesOnlyAfterFinalWave()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 75, characterAttack: 100, monsterAttack: 100);
        test.Monster.RoomId = test.Room.Id;
        test.Monster.WaveNumber = 1;
        test.Monster.Position = 1;
        test.Room.CurrentWaveNumber = 1;
        test.Room.TotalWaveCount = 2;
        var finalMonster = new Monster
        {
            RoomId = test.Room.Id, WaveNumber = 2, Position = 1, Name = "King Slime",
            Element = ElementType.Wind, Hp = 60, MaxHp = 60, Attack = 100, Defense = 2
        };
        test.Db.Monsters.Add(finalMonster);
        await test.Db.SaveChangesAsync();

        var (firstKill, firstError) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(firstError);
        Assert.Equal(RoomStatus.WaveTransition, firstKill!.RoomStatus);
        Assert.Equal(2, firstKill.CurrentWaveNumber);
        Assert.Equal(finalMonster.Id, test.Room.MonsterId);
        Assert.Equal(75, test.Character.Hp);
        Assert.Equal("Pending", (await test.Db.RewardRuns.SingleAsync()).Status);
        Assert.Single(await test.Db.RewardEvents.ToListAsync());

        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddMilliseconds(-100);
        test.Room.Version++;
        await test.Db.SaveChangesAsync();
        var (ready, readyError) = await test.Service.SyncRoomAsync(1);
        Assert.Null(readyError);
        Assert.Equal(RoomStatus.Cooldown, ready!.RoomStatus);
        Assert.InRange((ready.NextRoundAvailableAtUtc!.Value - ready.ServerTimeUtc).TotalSeconds, 4, 6);

        var (prepared, prepareError) = await test.Service.StartPreparationAsync(1, test.Token);
        Assert.Null(prepareError);
        Assert.Equal(RoomStatus.Cooldown, prepared!.RoomStatus);
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        var (victory, victoryError) = await test.Service.SyncRoomAsync(1);

        Assert.Null(victoryError);
        Assert.Equal(RoomStatus.BattleOver, victory!.RoomStatus);
        Assert.True(victory.IsVictory);
        Assert.Equal("Victory", (await test.Db.RewardRuns.SingleAsync()).Status);
        Assert.Equal(3, await test.Db.RewardEvents.CountAsync());
    }

    [Fact]
    public async Task MultiWaveAutoWaitsOnlyForRemainingCooldownAfterEnemyRefresh()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 75, characterAttack: 100, monsterAttack: 1);
        test.Monster.RoomId = test.Room.Id;
        test.Monster.WaveNumber = 1;
        test.Monster.Position = 1;
        test.Room.CurrentWaveNumber = 1;
        test.Room.TotalWaveCount = 2;
        test.Db.Monsters.Add(new Monster
        {
            RoomId = test.Room.Id, WaveNumber = 2, Position = 1, Name = "King Slime",
            Element = ElementType.Wind, Hp = 60, MaxHp = 60, Attack = 1, Defense = 2
        });
        var slot = await test.Db.RoomSlots.SingleAsync();
        slot.IsAutoEnabled = true;
        await test.Db.SaveChangesAsync();

        var (firstKill, firstError) = await test.Service.StartPreparationAsync(1, test.Token);

        Assert.Null(firstError);
        Assert.Equal(RoomStatus.WaveTransition, firstKill!.RoomStatus);
        var nextMonster = await test.Db.Monsters.SingleAsync(monster => monster.Id == test.Room.MonsterId);
        Assert.Equal(60, nextMonster.Hp);

        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddMilliseconds(-100);
        test.Room.Version++;
        await test.Db.SaveChangesAsync();
        var (waiting, waitingError) = await test.Service.SyncRoomAsync(1);

        Assert.Null(waitingError);
        Assert.Equal(RoomStatus.Cooldown, waiting!.RoomStatus);
        Assert.Equal(BattleRules.AutoRoundCooldownSeconds, test.Room.RoundCooldownDurationSeconds);
        Assert.InRange((waiting.NextRoundAvailableAtUtc!.Value - waiting.ServerTimeUtc).TotalSeconds, 14, 16);
        Assert.Equal(1, test.Room.RoundNumber);
        Assert.Equal(60, nextMonster.Hp);

        var (stillWaiting, stillWaitingError) = await test.Service.SyncRoomAsync(1);

        Assert.Null(stillWaitingError);
        Assert.Equal(RoomStatus.Cooldown, stillWaiting!.RoomStatus);
        Assert.Equal(1, test.Room.RoundNumber);
        Assert.Equal(60, nextMonster.Hp);

        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        test.Room.Version++;
        await test.Db.SaveChangesAsync();
        var (victory, victoryError) = await test.Service.SyncRoomAsync(1);

        Assert.Null(victoryError);
        Assert.Equal(RoomStatus.BattleOver, victory!.RoomStatus);
        Assert.True(victory.IsVictory);
        Assert.Equal(2, test.Room.RoundNumber);
        Assert.Equal(0, nextMonster.Hp);
    }

    [Fact]
    public async Task MonsterIntentExecutesOnceAndNextRoundIsPlannedBeforePlayerPrepares()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 10, characterDefense: 2);
        test.Monster.CombatProfileCode = "acid-slime";
        await test.Db.SaveChangesAsync();
        var progression = ProgressionTestFactory.Create();
        var rewards = RewardTestFactory.CreateService(test.Db, progression);
        var monsterCombat = new MonsterCombatService(test.Db, MonsterCombatTestFactory.CreateCatalog());
        var dungeonRun = new DungeonRunService(test.Db, rewards, monsterCombat);
        var service = new BattleService(test.Db,
            new UserService(test.Db, progression, SkillTestFactory.Create()), ConsumableTestFactory.Create(),
            SkillTestFactory.Create(), rewards, dungeonRun, monsterCombat);
        await monsterCombat.EnsureIntentAsync(test.Room, test.Monster);
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(RoomStatus.Cooldown, result!.RoomStatus);
        Assert.Equal(88, test.Character.Hp);
        Assert.Contains(result.Logs, log => log.Contains("使用 腐蚀喷射"));
        Assert.Equal("armor-break", (await test.Db.BattleStatusEffects.SingleAsync()).EffectCode);
        var nextIntent = await test.Db.MonsterIntents.SingleAsync();
        Assert.Equal(1, nextIntent.RoundNumber);
        Assert.Equal("BasicAttack", nextIntent.ActionType);

        var rooms = new RoomService(test.Db,
            new UserService(test.Db, progression, SkillTestFactory.Create()), progression,
            ConsumableTestFactory.Create(), SkillTestFactory.Create(), rewards, null, monsterCombat);
        var detail = await rooms.GetRoomDetailAsync(1, test.Token);
        Assert.Equal("普通攻击", detail!.MonsterIntent!.ActionName);
        Assert.Equal("破甲", Assert.Single(detail.Slots[0].StatusEffects).Name);
    }

    [Fact]
    public async Task AutomaticInterruptSkillCancelsTelegraphedMonsterSkillAndStartsBothCooldowns()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 10, characterDefense: 2);
        test.Monster.CombatProfileCode = "acid-slime";
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "knight-interrupt", autoUse: true);
        var progression = ProgressionTestFactory.Create();
        var rewards = RewardTestFactory.CreateService(test.Db, progression);
        var monsterCombat = new MonsterCombatService(test.Db, MonsterCombatTestFactory.CreateCatalog());
        var skills = SkillTestFactory.CreateResponses();
        var service = new BattleService(test.Db, new UserService(test.Db, progression, skills),
            ConsumableTestFactory.Create(), skills, rewards,
            new DungeonRunService(test.Db, rewards, monsterCombat), monsterCombat);
        await monsterCombat.EnsureIntentAsync(test.Room, test.Monster);
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(100, test.Character.Hp);
        Assert.Contains(result!.Logs, log => log.Contains("打断了 Slime"));
        Assert.Contains(result.Logs, log => log.Contains("已被打断"));
        Assert.Empty(await test.Db.BattleStatusEffects.ToListAsync());
        Assert.Single(await test.Db.BattleSkillCooldowns.ToListAsync());
        Assert.Single(await test.Db.BattleMonsterSkillCooldowns.ToListAsync());
        Assert.Equal("BasicAttack", (await test.Db.MonsterIntents.SingleAsync()).ActionType);
    }

    [Fact]
    public async Task AcolyteSilenceInterruptsNowAndTheNextInterruptibleIntent()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 10, monsterDefense: 99);
        test.Character.ProfessionCode = "acolyte";
        test.Character.Level = 10;
        test.Monster.CombatProfileCode = "rapid-slime";
        test.Monster.Hp = test.Monster.MaxHp = 500;
        test.Db.CharacterSkillTalents.AddRange(
            new CharacterSkillTalent { CharacterId = test.Character.Id, NodeCode = "acolyte-echo", PointsSpent = 1 },
            new CharacterSkillTalent { CharacterId = test.Character.Id, NodeCode = "acolyte-silence-talent", PointsSpent = 1 });
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "acolyte-silence", autoUse: true);

        var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();
        var monsterOptions = config.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!;
        monsterOptions.Skills.Add(new MonsterSkillOptions
            { Code = "rapid", Name = "迅捷喷射", Description = "每回合攻击。", DamagePowerPercent = 120 });
        monsterOptions.Profiles["rapid-slime"] = new MonsterCombatProfileOptions
            { SkillUseChancePercent = 100, Skills = [new MonsterProfileSkillOptions { Code = "rapid" }] };
        var monsterCatalog = new MonsterCombatCatalog(Options.Create(monsterOptions));
        var monsterCombat = new MonsterCombatService(test.Db, monsterCatalog);
        var skills = new SkillCatalog(Options.Create(config.GetSection(SkillOptions.SectionName).Get<SkillOptions>()!), monsterCatalog);
        var progression = ProgressionTestFactory.Create();
        var rewards = RewardTestFactory.CreateService(test.Db, progression);
        var service = new BattleService(test.Db, new UserService(test.Db, progression, skills),
            ConsumableTestFactory.Create(), skills, rewards,
            new DungeonRunService(test.Db, rewards, monsterCombat), monsterCombat);
        await monsterCombat.EnsureIntentAsync(test.Room, test.Monster);
        await test.Db.SaveChangesAsync();

        var (first, firstError) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(firstError);
        Assert.Contains(first!.Logs, log => log.Contains("沉默祷言") && log.Contains("打断"));
        Assert.Equal("acolyte-silence", (await test.Db.BattleStatusEffects.SingleAsync()).EffectCode);
        Assert.True((await test.Db.MonsterIntents.SingleAsync()).IsInterrupted);

        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        test.Room.Version++;
        await test.Db.SaveChangesAsync();
        var (second, secondError) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(secondError);
        Assert.Contains(second!.Logs, log => log.Contains("沉默影响"));
        Assert.Equal(100, test.Character.Hp);
        Assert.Empty(await test.Db.BattleStatusEffects.Where(effect => effect.EffectCode == "acolyte-silence").ToListAsync());
    }

    [Fact]
    public async Task AutomaticPurifyRemovesAllyDebuffBeforeEndOfRound()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 10, characterDefense: 2);
        test.Character.ProfessionCode = "cleric";
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "cleric-purify", autoUse: true);
        var progression = ProgressionTestFactory.Create();
        var rewards = RewardTestFactory.CreateService(test.Db, progression);
        var monsterCombat = new MonsterCombatService(test.Db, MonsterCombatTestFactory.CreateCatalog());
        var skills = SkillTestFactory.CreateResponses();
        await monsterCombat.ApplyStatusAsync(test.Room, "Character", test.Character.Id, "poison", 2, [], "Knight");
        await monsterCombat.EnsureIntentAsync(test.Room, test.Monster);
        await test.Db.SaveChangesAsync();
        var service = new BattleService(test.Db, new UserService(test.Db, progression, skills),
            ConsumableTestFactory.Create(), skills, rewards,
            new DungeonRunService(test.Db, rewards, monsterCombat), monsterCombat);

        var (result, error) = await service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("移除了") && log.Contains("中毒"));
        Assert.Empty(await test.Db.BattleStatusEffects.ToListAsync());
        Assert.Equal(90, test.Character.Hp);
    }

    [Fact]
    public async Task AutomaticDispelRemovesMonsterBuffAndPlayerStatusSkillAppliesDebuff()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1, characterDefense: 99);
        test.Character.ProfessionCode = "cleric";
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "cleric-dispel", autoUse: true);
        var progression = ProgressionTestFactory.Create();
        var rewards = RewardTestFactory.CreateService(test.Db, progression);
        var monsterCombat = new MonsterCombatService(test.Db, MonsterCombatTestFactory.CreateCatalog());
        var clericSkills = SkillTestFactory.CreateResponses();
        await monsterCombat.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id, "slime-shell", 2, [], "Slime");
        await monsterCombat.EnsureIntentAsync(test.Room, test.Monster);
        await test.Db.SaveChangesAsync();
        var service = new BattleService(test.Db, new UserService(test.Db, progression, clericSkills),
            ConsumableTestFactory.Create(), clericSkills, rewards,
            new DungeonRunService(test.Db, rewards, monsterCombat), monsterCombat);

        var (result, error) = await service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("驱散了") && log.Contains("黏液硬化"));
        Assert.Empty(await test.Db.BattleStatusEffects.ToListAsync());

        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        test.Character.ProfessionCode = "knight";
        var slot = await test.Db.CharacterSkillSlots.SingleAsync();
        slot.SkillCode = "knight-break";
        slot.AutoUseEnabled = true;
        await test.Db.SaveChangesAsync();
        var knightSkills = SkillTestFactory.CreateResponses();
        service = new BattleService(test.Db, new UserService(test.Db, progression, knightSkills),
            ConsumableTestFactory.Create(), knightSkills, rewards,
            new DungeonRunService(test.Db, rewards, monsterCombat), monsterCombat);

        var (second, secondError) = await service.StartPreparationAsync(1, test.Token);

        Assert.Null(secondError);
        Assert.Contains(second!.Logs, log => log.Contains("获得 破甲"));
        Assert.Equal("armor-break", (await test.Db.BattleStatusEffects.SingleAsync()).EffectCode);
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
        Assert.Equal(0, second.Hp);
        Assert.Contains(result!.Logs, x => x.Contains("普通攻击 2号位 Mage"));
    }

    [Fact]
    public async Task SetSlotAutoAsync_DuringPreparation_WhenItConfirmsLastMember_ExecutesRound()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1, characterDefense: 99);
        var other = new Character { Id = 2, UserId = 2, Name = "Mage", Hp = 100, MaxHp = 100, Attack = 10};
        test.Db.AddRange(
            new User { Id = 2, UserName = "other", PasswordHash = "x", ActiveCharacterId = 2 },
            other,
            new RoomSlot { RoomId = 1, SlotIndex = 2, UserId = 2, CharacterId = 2 },
            new UserDungeonClear { UserId = 2, DungeonId = 1, ClearedAtUtc = DateTime.UtcNow },
            new CharacterBattleMilestone { CharacterId = 2, Kind = BattleMilestoneService.DungeonClearKind, TargetCode = "slime-field", Count = 1, FirstAtUtc = DateTime.UtcNow, LastAtUtc = DateTime.UtcNow },
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

    [Fact]
    public async Task ArcaneMasteryAddsAFourthIndependentlyResolvedBarrageHit()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 20, monsterAttack: 1, monsterDefense: 0);
        test.Character.ProfessionCode = "mage";
        test.Character.Level = 10;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Db.CharacterSkillTalents.AddRange(
            new CharacterSkillTalent { CharacterId = 1, NodeCode = "mage-arcane-insight", PointsSpent = 1 },
            new CharacterSkillTalent { CharacterId = 1, NodeCode = "mage-arcane-training", PointsSpent = 2 },
            new CharacterSkillTalent { CharacterId = 1, NodeCode = "mage-barrage-talent", PointsSpent = 1 },
            new CharacterSkillTalent { CharacterId = 1, NodeCode = "mage-precision", PointsSpent = 2 },
            new CharacterSkillTalent { CharacterId = 1, NodeCode = "mage-arcane-mastery", PointsSpent = 1 });
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "mage-arcane-barrage", autoUse: true);
        var (service, _) = CreateProductionSoulBattleService(test);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(3, result!.Logs.Count(log => log.Contains("使用 奥术弹幕 攻击")));
        Assert.Single(result.Logs, log => log.Contains("奥术掌握追加飞弹"));
    }

    [Fact]
    public async Task HunterRelentlessTalentAppliesTwoPoisonStacksWithVenomArrow()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 10, monsterAttack: 1, monsterDefense: 0);
        test.Character.ProfessionCode = "hunter";
        test.Character.Level = 10;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Db.CharacterSkillTalents.AddRange(
            new CharacterSkillTalent { CharacterId = 1, NodeCode = "hunter-steady-hand", PointsSpent = 1 },
            new CharacterSkillTalent { CharacterId = 1, NodeCode = "hunter-venom-talent", PointsSpent = 1 },
            new CharacterSkillTalent { CharacterId = 1, NodeCode = "hunter-relentless", PointsSpent = 1 });
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "hunter-venom-arrow", autoUse: true);
        var (service, _) = CreateProductionSoulBattleService(test);

        var (_, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        var poison = await test.Db.BattleStatusEffects.SingleAsync(effect => effect.EffectCode == "poison");
        Assert.Equal(2, poison.Stacks);
    }

    [Theory]
    [InlineData("mage", "mage-frost-ward", "mage-frozen-heart")]
    [InlineData("rogue", "rogue-evasion", "rogue-escape-artist")]
    public async Task DefensiveCapstonesCleanseOneSelfDebuff(string profession, string skillCode, string talentCode)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 50, characterAttack: 1, monsterAttack: 1);
        test.Character.ProfessionCode = profession;
        test.Character.Level = 10;
        test.Db.CharacterSkillTalents.Add(new CharacterSkillTalent
            { CharacterId = 1, NodeCode = talentCode, PointsSpent = 1 });
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, skillCode, autoUse: true, threshold: 70);
        var (service, monsterCombat) = CreateProductionSoulBattleService(test);
        await monsterCombat.ApplyStatusAsync(test.Room, "Character", 1, "poison", 2, [], test.Character.Name);
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("借助") && log.Contains("移除了 中毒"));
        Assert.DoesNotContain(await test.Db.BattleStatusEffects.ToListAsync(), effect => effect.EffectCode == "poison");
    }

    [Fact]
    public async Task ArcanistOverchargeReducesOtherDamageSkillCooldowns()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        test.Character.ProfessionCode = "mage";
        test.Character.AdvancedProfessionCode = "arcanist";
        test.Character.Level = 10;
        test.Db.BattleSkillCooldowns.Add(new BattleSkillCooldown
            { RoomId = 1, CharacterId = 1, SkillCode = "mage-arcane-bolt", ReadyAtRound = 5 });
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "arcanist-overcharge", autoUse: true);
        var (service, _) = CreateProductionSoulBattleService(test);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("奥能超载") && log.Contains("冷却缩短 1 回合"));
        Assert.Equal(4, (await test.Db.BattleSkillCooldowns.SingleAsync(entry => entry.SkillCode == "mage-arcane-bolt")).ReadyAtRound);
    }

    [Fact]
    public async Task StableChannelingReducesDamageCooldownAfterSpellbreakDispelsABuff()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        test.Character.ProfessionCode = "mage";
        test.Character.Level = 10;
        test.Db.CharacterSkillTalents.AddRange(
            new CharacterSkillTalent { CharacterId = 1, NodeCode = "mage-flow", PointsSpent = 1 },
            new CharacterSkillTalent { CharacterId = 1, NodeCode = "mage-spellbreak-talent", PointsSpent = 1 },
            new CharacterSkillTalent { CharacterId = 1, NodeCode = "mage-stable-channeling", PointsSpent = 1 });
        test.Db.BattleSkillCooldowns.Add(new BattleSkillCooldown
            { RoomId = 1, CharacterId = 1, SkillCode = "mage-arcane-bolt", ReadyAtRound = 5 });
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "mage-spellbreak", autoUse: true);
        var (service, monsterCombat) = CreateProductionSoulBattleService(test);
        await monsterCombat.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id, "slime-shell", 2, [], test.Monster.Name);
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("法术反制") && log.Contains("驱散了"));
        Assert.Contains(result.Logs, log => log.Contains("稳定引导") && log.Contains("冷却缩短 1 回合"));
        Assert.Equal(4, (await test.Db.BattleSkillCooldowns.SingleAsync(entry => entry.SkillCode == "mage-arcane-bolt")).ReadyAtRound);
    }

    [Fact]
    public async Task PredatorIncreasesSkillDamageAgainstAHuntersMarkedTarget()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 20, monsterAttack: 1, monsterDefense: 0);
        test.Character.ProfessionCode = "hunter";
        test.Character.Level = 10;
        test.Monster.Hp = test.Monster.MaxHp = 500;
        test.Db.CharacterSkillTalents.Add(new CharacterSkillTalent
            { CharacterId = 1, NodeCode = "hunter-predator", PointsSpent = 1 });
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "hunter-quick-shot", autoUse: true);
        var (service, monsterCombat) = CreateProductionSoulBattleService(test);
        await monsterCombat.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id, "hunters-mark", 2, [], test.Monster.Name);
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("快速射击 攻击") && log.Contains("造成 30 点伤害"));
    }

    [Fact]
    public async Task OpportunistExposesTheMonsterAfterGougeInterruptsItsIntent()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 10, monsterDefense: 99);
        test.Character.ProfessionCode = "rogue";
        test.Character.Level = 10;
        test.Monster.CombatProfileCode = "rapid-slime";
        test.Monster.Hp = test.Monster.MaxHp = 500;
        test.Db.CharacterSkillTalents.AddRange(
            new CharacterSkillTalent { CharacterId = 1, NodeCode = "rogue-light-fingers", PointsSpent = 1 },
            new CharacterSkillTalent { CharacterId = 1, NodeCode = "rogue-poison-talent", PointsSpent = 1 },
            new CharacterSkillTalent { CharacterId = 1, NodeCode = "rogue-gouge-talent", PointsSpent = 1 },
            new CharacterSkillTalent { CharacterId = 1, NodeCode = "rogue-opportunist", PointsSpent = 1 });
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "rogue-gouge", autoUse: true);

        var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();
        var monsterOptions = configuration.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!;
        monsterOptions.Skills.Add(new MonsterSkillOptions
            { Code = "rapid", Name = "迅捷喷射", Description = "每回合攻击。", DamagePowerPercent = 120 });
        monsterOptions.Profiles["rapid-slime"] = new MonsterCombatProfileOptions
            { SkillUseChancePercent = 100, Skills = [new MonsterProfileSkillOptions { Code = "rapid" }] };
        var monsterCatalog = new MonsterCombatCatalog(Options.Create(monsterOptions));
        var monsterCombat = new MonsterCombatService(test.Db, monsterCatalog);
        var skills = new SkillCatalog(Options.Create(
            configuration.GetSection(SkillOptions.SectionName).Get<SkillOptions>()!), monsterCatalog);
        var progression = ProgressionTestFactory.Create();
        var rewards = RewardTestFactory.CreateService(test.Db, progression);
        var service = new BattleService(test.Db, new UserService(test.Db, progression, skills),
            ConsumableTestFactory.Create(), skills, rewards,
            new DungeonRunService(test.Db, rewards, monsterCombat), monsterCombat);
        await monsterCombat.EnsureIntentAsync(test.Room, test.Monster);
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("凿击") && log.Contains("打断了"));
        Assert.Contains(result.Logs, log => log.Contains("获得 破绽"));
        Assert.Contains(await test.Db.BattleStatusEffects.ToListAsync(), effect => effect.EffectCode == "rogue-opening");
    }

    [Fact]
    public async Task PromotionFinishersOnlyReceiveTheirConfiguredConditionalDamageBonus()
    {
        await using var marksman = await BattleTestContext.CreateAsync(characterAttack: 20, monsterAttack: 1, monsterDefense: 0);
        marksman.Character.ProfessionCode = "hunter";
        marksman.Character.AdvancedProfessionCode = "marksman";
        marksman.Character.Level = 10;
        marksman.Monster.Hp = marksman.Monster.MaxHp = 500;
        await marksman.Db.SaveChangesAsync();
        await marksman.AddSkillAsync(marksman.Character, 1, "marksman-sniper-shot", autoUse: true);
        var (marksmanService, marksmanCombat) = CreateProductionSoulBattleService(marksman);
        await marksmanCombat.ApplyStatusAsync(marksman.Room, "Monster", 1, "hunters-mark", 2, [], marksman.Monster.Name);
        await marksman.Db.SaveChangesAsync();

        var (markedResult, markedError) = await marksmanService.StartPreparationAsync(1, marksman.Token);

        Assert.Null(markedError);
        Assert.Contains(markedResult!.Logs, log => log.Contains("致命狙击 攻击") && log.Contains("造成 49 点伤害"));

        await using var assassin = await BattleTestContext.CreateAsync(characterAttack: 20, monsterAttack: 1, monsterDefense: 0);
        assassin.Character.ProfessionCode = "rogue";
        assassin.Character.AdvancedProfessionCode = "assassin";
        assassin.Character.Level = 10;
        assassin.Monster.MaxHp = 500;
        assassin.Monster.Hp = 175;
        await assassin.Db.SaveChangesAsync();
        await assassin.AddSkillAsync(assassin.Character, 1, "assassin-deathblow", autoUse: true);
        var (assassinService, _) = CreateProductionSoulBattleService(assassin);

        var (executeResult, executeError) = await assassinService.StartPreparationAsync(1, assassin.Token);

        Assert.Null(executeError);
        Assert.Contains(executeResult!.Logs, log => log.Contains("绝命一击 攻击") && log.Contains("造成 48 点伤害"));
    }

    [Fact]
    public async Task HardenedHunterGainsResilienceAfterFieldMend()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 50, characterAttack: 1, monsterAttack: 1);
        test.Character.ProfessionCode = "hunter";
        test.Character.Level = 10;
        test.Db.CharacterSkillTalents.Add(new CharacterSkillTalent
            { CharacterId = 1, NodeCode = "hunter-hardened", PointsSpent = 1 });
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "hunter-field-mend", autoUse: true, threshold: 70);
        var (service, _) = CreateProductionSoulBattleService(test);

        var (result, error) = await service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("获得 荒野韧性"));
        Assert.Contains(await test.Db.BattleStatusEffects.ToListAsync(), effect => effect.EffectCode == "hunter-resilience");
    }

    [Fact]
    public async Task PriestGroupHealAlsoCleansesTheFirstDebuffedAlly()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 50, characterAttack: 1, monsterAttack: 1);
        test.Character.ProfessionCode = "acolyte";
        test.Character.AdvancedProfessionCode = "priest";
        test.Character.Level = 10;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "priest-group-heal", autoUse: true, threshold: 70);
        var (service, monsterCombat) = CreateProductionSoulBattleService(test);
        await monsterCombat.ApplyStatusAsync(test.Room, "Character", 1, "poison", 2, [], test.Character.Name);
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("群体治疗") && log.Contains("恢复"));
        Assert.Contains(result.Logs, log => log.Contains("群体治疗") && log.Contains("移除了") && log.Contains("中毒"));
        Assert.DoesNotContain(await test.Db.BattleStatusEffects.ToListAsync(), effect => effect.EffectCode == "poison");
    }

    [Fact]
    public async Task RelentlessRogueAddsAThirdBladeFlurryHit()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 20, monsterAttack: 1, monsterDefense: 0);
        test.Character.ProfessionCode = "rogue";
        test.Character.Level = 10;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Db.CharacterSkillTalents.AddRange(
            new CharacterSkillTalent { CharacterId = 1, NodeCode = "rogue-killer-instinct", PointsSpent = 1 },
            new CharacterSkillTalent { CharacterId = 1, NodeCode = "rogue-blade-training", PointsSpent = 2 },
            new CharacterSkillTalent { CharacterId = 1, NodeCode = "rogue-flurry-talent", PointsSpent = 1 },
            new CharacterSkillTalent { CharacterId = 1, NodeCode = "rogue-relentless-assault", PointsSpent = 1 });
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "rogue-blade-flurry", autoUse: true);
        var (service, _) = CreateProductionSoulBattleService(test);

        var (result, error) = await service.StartPreparationAsync(1, test.Token);

        Assert.Null(error);
        Assert.Equal(2, result!.Logs.Count(log => log.Contains("使用 刀锋乱舞 攻击")));
        Assert.Single(result.Logs, log => log.Contains("夺命连攻追加攻击"));
    }

    private static (BattleService Service, MonsterCombatService MonsterCombat) CreateProductionSoulBattleService(
        BattleTestContext test)
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();
        var monsterCatalog = new MonsterCombatCatalog(Options.Create(
            configuration.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!));
        var monsterCombat = new MonsterCombatService(test.Db, monsterCatalog);
        var skills = new SkillCatalog(Options.Create(
            configuration.GetSection(SkillOptions.SectionName).Get<SkillOptions>()!), monsterCatalog);
        var soulImprints = new SoulImprintCatalog(Options.Create(
            configuration.GetSection(SoulImprintOptions.SectionName).Get<SoulImprintOptions>()!));
        var progression = ProgressionTestFactory.Create();
        var rewards = RewardTestFactory.CreateService(test.Db, progression);
        var service = new BattleService(test.Db, new UserService(test.Db, progression, skills),
            ConsumableTestFactory.Create(), skills, rewards,
            new DungeonRunService(test.Db, rewards, monsterCombat), monsterCombat,
            soulImprintCatalog: soulImprints);
        return (service, monsterCombat);
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
            Service = new BattleService(db, new UserService(db, progression, SkillTestFactory.Create()), ConsumableTestFactory.Create(), SkillTestFactory.Create(), RewardTestFactory.CreateService(db, progression), soulImprintCatalog: SoulImprintTestFactory.Create());
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

        public async Task AddOperationPotionAsync(Character character, int quantity)
        {
            Db.CharacterItemStacks.Add(new CharacterItemStack
            {
                CharacterId = character.Id, ItemCode = "northshire-battle-draught", Quantity = quantity
            });
            Db.CharacterConsumableSlots.Add(new CharacterConsumableSlot
            {
                CharacterId = character.Id,
                SlotIndex = Game.Shared.ConsumableRules.OperationPotionSlotIndex,
                ItemCode = "northshire-battle-draught"
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
            return new RoomService(Db, new UserService(Db, progression, SkillTestFactory.Create()), progression, ConsumableTestFactory.Create(), SkillTestFactory.Create(), RewardTestFactory.CreateService(Db, progression), soulImprintCatalog: SoulImprintTestFactory.Create()).GetRoomDetailAsync(Room.Id, Token);
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
            var character = new Character { UserId = 1, Name = name, Hp = hp, MaxHp = 100, Attack = attack};
            Db.Characters.Add(character);
            await Db.SaveChangesAsync();
            Db.RoomSlots.Add(new RoomSlot { RoomId = Room.Id, SlotIndex = slotIndex, CharacterId = character.Id, UserId = 1 });
            await Db.SaveChangesAsync();
            return character;
        }

        public async Task<RoomSlot> AddOtherMemberAsync()
        {
            var character = new Character { Id = 2, UserId = 2, Name = "Mage", Hp = 100, MaxHp = 100, Attack = 10};
            var slot = new RoomSlot { RoomId = Room.Id, SlotIndex = 2, CharacterId = 2, UserId = 2 };
            Db.AddRange(new User { Id = 2, UserName = "other", PasswordHash = "x", ActiveCharacterId = 2 }, character, slot, new UserLoginSession { UserId = 2, Token = "other-token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
            Db.UserDungeonClears.Add(new UserDungeonClear { UserId = 2, DungeonId = 1, ClearedAtUtc = DateTime.UtcNow });
            Db.CharacterBattleMilestones.Add(new CharacterBattleMilestone { CharacterId = 2, Kind = BattleMilestoneService.DungeonClearKind, TargetCode = "slime-field", Count = 1, FirstAtUtc = DateTime.UtcNow, LastAtUtc = DateTime.UtcNow });
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
            var character = new Character { Id = 1, UserId = 1, Name = "Knight", Hp = characterHp, MaxHp = 100, Attack = characterAttack};
            var monster = new Monster { Id = 1, Name = "Slime", Hp = 50, MaxHp = 50, Attack = monsterAttack, Defense = monsterDefense };
            var room = new Room { Id = 1, DungeonId = 1, MonsterId = 1, OwnerUserId = 1, SlotCount = 5, Status = RoomStatus.NotStarted, IsPublic = true };
            db.AddRange(new Dungeon { Id = 1, Code = "slime-field", Name = "史莱姆平原", MonsterName = "Slime", MonsterMaxHp = 50, MonsterAttack = monsterAttack, MonsterDefense = monsterDefense, SlotCount = 5, SortOrder = 1 }, user, character, monster, room, new UserDungeonClear { UserId = 1, DungeonId = 1, ClearedAtUtc = DateTime.UtcNow }, new CharacterBattleMilestone { CharacterId = 1, Kind = BattleMilestoneService.DungeonClearKind, TargetCode = "slime-field", Count = 1, FirstAtUtc = DateTime.UtcNow, LastAtUtc = DateTime.UtcNow }, new RoomSlot { Id = 1, RoomId = 1, SlotIndex = 1, UserId = 1, CharacterId = 1, IsMainControl = true }, new UserLoginSession { Id = 1, UserId = 1, Token = "token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) });
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
