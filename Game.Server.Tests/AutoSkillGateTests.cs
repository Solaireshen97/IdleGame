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
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangingMainControlPreservesOwnAutoPreferenceAndIndividualUnlocks(bool autoEnabled)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var guest = await test.AddOtherMemberAsync();
        var newcomer = await test.AddSlotAsync(3, "Newcomer");
        var oldMain = await test.Db.RoomSlots.SingleAsync(slot => slot.IsMainControl);
        var newMain = await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == newcomer.Id);
        oldMain.IsAutoEnabled = autoEnabled;
        newMain.IsAutoEnabled = !autoEnabled;
        guest.IsAutoEnabled = !autoEnabled;
        await test.Db.SaveChangesAsync();
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        var rooms = new RoomService(test.Db, new UserService(test.Db, progression, skills), progression,
            ConsumableTestFactory.Create(), skills, RewardTestFactory.CreateService(test.Db, progression));

        var (detail, error) = await rooms.SetMainControlAsync(test.Room.Id,
            new SetMainControlRequest { CharacterId = newcomer.Id }, test.Token);

        Assert.Null(error);
        Assert.False(oldMain.IsMainControl);
        Assert.True(newMain.IsMainControl);
        Assert.Equal(autoEnabled, oldMain.IsAutoEnabled);
        Assert.Equal(autoEnabled, newMain.IsAutoEnabled);
        Assert.Equal(!autoEnabled, guest.IsAutoEnabled);
        Assert.Equal(autoEnabled, detail!.IsCurrentUserAutoEnabled);
        var veteranState = detail.Slots.Single(slot => slot.CharacterId == test.Character.Id);
        var newcomerState = detail.Slots.Single(slot => slot.CharacterId == newcomer.Id);
        Assert.True(veteranState.IsAutoUnlockedForCurrentUser);
        Assert.Equal(autoEnabled, veteranState.IsAutoEnabled);
        Assert.False(newcomerState.IsAutoUnlockedForCurrentUser);
        Assert.False(newcomerState.IsAutoEnabled);
        Assert.False(detail.IsAllAliveMembersAuto);
    }

    [Theory]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, true)]
    public async Task SoulAutoGateRequiresCharacterUnlockAndOwnAutoSwitch(
        bool autoEnabled, bool unlocked, bool preparationTimeout, bool expectedCast)
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Room.RoundNumber = 3;
        var owner = await test.Db.RoomSlots.SingleAsync();
        owner.IsAutoEnabled = autoEnabled;
        if (!unlocked)
            test.Db.CharacterBattleMilestones.RemoveRange(await test.Db.CharacterBattleMilestones.ToListAsync());
        test.Db.CharacterSoulImprints.Add(new CharacterSoulImprint
        {
            CharacterId = test.Character.Id, SoulImprintCode = "deep-core",
            EquippedSlotIndex = SoulImprintRules.SlotIndex, AutoUseEnabled = true
        });
        if (preparationTimeout)
            test.Room.PreparationStartedAtUtc = DateTime.UtcNow.AddSeconds(-BattleRules.PreparationTimeoutSeconds - 1);
        await test.Db.SaveChangesAsync();

        var (result, error) = preparationTimeout
            ? await test.Service.SyncRoomAsync(test.Room.Id)
            : await test.Service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(4, test.Room.RoundNumber);
        Assert.Equal(expectedCast, result!.Logs.Any(log => log.Contains("释放魂印")));
        Assert.Equal(expectedCast, await test.Db.BattleSkillCooldowns.AnyAsync(cooldown =>
            cooldown.SkillCode == SoulImprintRules.CooldownCode("deep-core")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualSkillAndSoulQueuesStillExecuteWithoutAutoUnlock(bool preparationTimeout)
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Room.RoundNumber = 3;
        await test.AddSkillAsync(test.Character, 1, "knight-strike", autoUse: false);
        test.Db.CharacterBattleMilestones.RemoveRange(await test.Db.CharacterBattleMilestones.ToListAsync());
        test.Db.CharacterSoulImprints.Add(new CharacterSoulImprint
        {
            CharacterId = test.Character.Id, SoulImprintCode = "deep-core",
            EquippedSlotIndex = SoulImprintRules.SlotIndex, AutoUseEnabled = false
        });
        await test.Db.SaveChangesAsync();
        var (skillQueued, skillError) = await test.Service.QueueSkillAsync(new QueueSkillRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 1, IsQueued = true
        }, test.Token);
        var (soulQueued, soulError) = await test.Service.QueueSoulImprintAsync(new QueueSoulImprintRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, IsQueued = true
        }, test.Token);
        Assert.True(skillQueued);
        Assert.Null(skillError);
        Assert.True(soulQueued);
        Assert.Null(soulError);
        if (preparationTimeout)
        {
            test.Room.PreparationStartedAtUtc = DateTime.UtcNow.AddSeconds(-BattleRules.PreparationTimeoutSeconds - 1);
            await test.Db.SaveChangesAsync();
        }

        var (result, error) = preparationTimeout
            ? await test.Service.SyncRoomAsync(test.Room.Id)
            : await test.Service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("使用 盾击"));
        Assert.Contains(result.Logs, log => log.Contains("释放魂印"));
        var owner = await test.Db.RoomSlots.SingleAsync();
        Assert.False(owner.IsAutoEnabled);
        Assert.Equal(0, owner.PendingSkillSlotMask);
        Assert.False(owner.IsSoulImprintQueued);
    }

    [Fact]
    public async Task OwnerAutoSwitchOffSuppressesClearedSecondaryEvenWithStaleSlotFlag()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        var secondary = await test.AddSlotAsync(2, "Secondary");
        await test.AddSkillAsync(secondary, 1, "knight-strike", autoUse: true);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        var main = await test.Db.RoomSlots.SingleAsync(slot => slot.IsMainControl);
        main.IsAutoEnabled = false;
        var secondarySlot = await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == secondary.Id);
        secondarySlot.IsAutoEnabled = true;
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.DoesNotContain(result!.Logs, log => log.Contains("使用 盾击"));
        Assert.Empty(await test.Db.BattleSkillCooldowns.ToListAsync());
    }

    [Fact]
    public async Task LegacyOwnerAutoSwitchOnAppliesOnlyToIndividuallyClearedCharacters()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        var veteran = await test.AddSlotAsync(2, "Veteran");
        var newcomer = await test.AddSlotAsync(3, "Newcomer");
        foreach (var character in new[] { test.Character, veteran, newcomer })
            await test.AddSkillAsync(character, 1, "knight-strike", autoUse: true);
        test.Db.CharacterBattleMilestones.RemoveRange(await test.Db.CharacterBattleMilestones
            .Where(milestone => milestone.CharacterId == newcomer.Id).ToListAsync());
        var slots = await test.Db.RoomSlots.ToListAsync();
        foreach (var slot in slots) slot.IsAutoEnabled = slot.IsMainControl;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("1号位") && log.Contains("使用 盾击"));
        Assert.Contains(result.Logs, log => log.Contains("2号位") && log.Contains("使用 盾击"));
        Assert.DoesNotContain(result.Logs, log => log.Contains("3号位") && log.Contains("使用 盾击"));
        var cooldownCharacters = await test.Db.BattleSkillCooldowns.Select(cooldown => cooldown.CharacterId).ToListAsync();
        Assert.Equal(new[] { test.Character.Id, veteran.Id }, cooldownCharacters.OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task AutoSwitchFromSecondaryUpdatesOnlyTheRequestingUsersCharacters()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        var guest = await test.AddOtherMemberAsync();
        var secondary = await test.AddSlotAsync(3, "Secondary");
        await test.AddSkillAsync(secondary, 1, "knight-strike", autoUse: true);
        test.Db.CharacterBattleMilestones.RemoveRange(await test.Db.CharacterBattleMilestones
            .Where(milestone => milestone.CharacterId == test.Character.Id).ToListAsync());
        var slots = await test.Db.RoomSlots.ToListAsync();
        foreach (var slot in slots) slot.IsAutoEnabled = false;
        guest.IsAutoEnabled = true;
        test.Room.Status = RoomStatus.Cooldown;
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(30);
        test.Room.RoundCooldownDurationSeconds = BattleRules.RoundCooldownSeconds;
        await test.Db.SaveChangesAsync();

        var (_, enableError) = await test.Service.SetSlotAutoAsync(test.Room.Id,
            new SetSlotAutoRequest { SlotIndex = 3, IsAutoEnabled = true }, test.Token);

        Assert.Null(enableError);
        Assert.All(slots.Where(slot => slot.UserId == 1), slot => Assert.True(slot.IsAutoEnabled));
        Assert.True(guest.IsAutoEnabled);

        var (_, disableError) = await test.Service.SetSlotAutoAsync(test.Room.Id,
            new SetSlotAutoRequest { SlotIndex = 3, IsAutoEnabled = false }, test.Token);

        Assert.Null(disableError);
        Assert.All(slots.Where(slot => slot.UserId == 1), slot => Assert.False(slot.IsAutoEnabled));
        Assert.True(guest.IsAutoEnabled);
        Assert.Equal(0, test.Room.RoundNumber);
    }

    [Fact]
    public async Task DeadMainCharacterStillSuppliesTheOwnerGroupsExplicitAutoSwitch()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        var secondary = await test.AddSlotAsync(2, "Secondary");
        await test.AddSkillAsync(secondary, 1, "knight-strike", autoUse: true);
        var main = await test.Db.RoomSlots.SingleAsync(slot => slot.IsMainControl);
        var secondarySlot = await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == secondary.Id);
        main.IsAutoEnabled = true;
        secondarySlot.IsAutoEnabled = false;
        test.Character.Hp = 0;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.SyncRoomAsync(test.Room.Id);

        Assert.Null(error);
        Assert.Equal(1, test.Room.RoundNumber);
        Assert.Contains(result!.Logs, log => log.Contains("2号位") && log.Contains("使用 盾击"));
        Assert.DoesNotContain(result.Logs, log => log.Contains("1号位") && log.Contains("普通攻击"));
    }

    [Fact]
    public async Task MixedTeamTimeoutOnlyLabelsManualMemberAndKeepsAutoTeammatesSkills()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        var guest = await test.AddOtherMemberAsync();
        var guestCharacter = await test.Db.Characters.SingleAsync(character => character.Id == guest.CharacterId);
        await test.AddSkillAsync(test.Character, 1, "knight-strike", autoUse: true);
        await test.AddSkillAsync(guestCharacter, 1, "knight-strike", autoUse: true);
        guest.IsAutoEnabled = false;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Room.PreparationStartedAtUtc = DateTime.UtcNow.AddSeconds(-BattleRules.PreparationTimeoutSeconds - 1);
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.SyncRoomAsync(test.Room.Id);

        Assert.Null(error);
        Assert.Equal(1, test.Room.RoundNumber);
        Assert.Contains(result!.Logs, log => log.Contains("2号位") && log.Contains("准备超时"));
        Assert.DoesNotContain(result.Logs, log => log.Contains("1号位") && log.Contains("准备超时"));
        Assert.Contains(result.Logs, log => log.Contains("1号位") && log.Contains("使用 盾击"));
        Assert.DoesNotContain(result.Logs, log => log.Contains("2号位") && log.Contains("使用 盾击"));
        Assert.All(await test.Db.RoomSlots.ToListAsync(), slot => Assert.False(slot.IsTemporaryAuto));
    }

    [Fact]
    public async Task DisablingOwnAutoWithdrawsConfirmationButPreservesManualQueuesAndPreparationClock()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        var guest = await test.AddOtherMemberAsync();
        var secondary = await test.AddSlotAsync(3, "Secondary");
        await test.AddSkillAsync(test.Character, 1, "knight-strike", autoUse: true);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Room.RoundNumber = 3;
        test.Db.CharacterSoulImprints.Add(new CharacterSoulImprint
        {
            CharacterId = test.Character.Id, SoulImprintCode = "deep-core",
            EquippedSlotIndex = SoulImprintRules.SlotIndex, AutoUseEnabled = true
        });
        await test.Db.SaveChangesAsync();
        var (skillQueued, skillError) = await test.Service.QueueSkillAsync(new QueueSkillRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 1, IsQueued = true
        }, test.Token);
        var (soulQueued, soulError) = await test.Service.QueueSoulImprintAsync(new QueueSoulImprintRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, IsQueued = true
        }, test.Token);
        Assert.True(skillQueued);
        Assert.Null(skillError);
        Assert.True(soulQueued);
        Assert.Null(soulError);
        var slots = await test.Db.RoomSlots.ToListAsync();
        foreach (var slot in slots.Where(slot => slot.UserId == 1))
        {
            slot.IsAutoEnabled = true;
            slot.IsConfirmed = true;
        }
        guest.IsAutoEnabled = false;
        guest.IsConfirmed = false;
        test.Room.Status = RoomStatus.Preparing;
        var preparationStarted = DateTime.UtcNow.AddSeconds(-8);
        test.Room.PreparationStartedAtUtc = preparationStarted;
        await test.Db.SaveChangesAsync();

        var (_, error) = await test.Service.SetSlotAutoAsync(test.Room.Id,
            new SetSlotAutoRequest { SlotIndex = 3, IsAutoEnabled = false }, test.Token);

        Assert.Null(error);
        Assert.All(slots.Where(slot => slot.UserId == 1), slot =>
        {
            Assert.False(slot.IsAutoEnabled);
            Assert.False(slot.IsConfirmed);
        });
        Assert.False(guest.IsAutoEnabled);
        Assert.False(guest.IsConfirmed);
        Assert.Equal(preparationStarted, test.Room.PreparationStartedAtUtc);
        var main = slots.Single(slot => slot.IsMainControl);
        Assert.Equal(SkillRules.SlotMask(1), main.PendingSkillSlotMask);
        Assert.True(main.IsSoulImprintQueued);
        Assert.Equal(3, test.Room.RoundNumber);

        var (_, ownerError) = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);
        var (result, guestError) = await test.Service.StartPreparationAsync(test.Room.Id, "other-token");
        Assert.Null(ownerError);
        Assert.Null(guestError);
        Assert.Contains(result!.Logs, log => log.Contains("使用 盾击"));
        Assert.Contains(result.Logs, log => log.Contains("释放魂印"));
    }
}
