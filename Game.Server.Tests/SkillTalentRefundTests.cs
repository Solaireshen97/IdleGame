using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public sealed partial class SkillTalentTreeTests
{
    [Fact]
    public async Task RefundReturnsOneRankAndOnePointAndCannotRefundAnEmptyNode()
    {
        await using var test = await FormalTreeContext.CreateAsync("swordsman", 10, 9);
        await PurchasePathAsync(test, "sword-edge", "sword-rhythm", "sword-combat-training", "sword-combat-training");
        var version = test.Character.Version;

        var (response, error) = await test.Service.RefundTalentNodeAsync("token", 1, "sword-combat-training");

        Assert.Null(error);
        Assert.Equal(6, response!.TalentPoints);
        Assert.Equal(1, response.TalentNodes.Single(node => node.Code == "sword-combat-training").Rank);
        Assert.Equal(4, test.Character.TalentSkillDamagePercent);
        Assert.Equal(3, test.Character.TalentNormalAttackPercent);
        Assert.Equal(version + 1, test.Character.Version);
        Assert.Equal(1, (await test.Db.CharacterSkillTalents.SingleAsync(node => node.NodeCode == "sword-combat-training")).PointsSpent);

        Assert.Null((await test.Service.RefundTalentNodeAsync("token", 1, "sword-combat-training")).Error);
        Assert.Equal(7, test.Character.TalentPoints);
        Assert.Equal(0, test.Character.TalentSkillDamagePercent);
        Assert.Equal("SkillTalentNotLearned", (await test.Service.RefundTalentNodeAsync("token", 1, "sword-combat-training")).Error);
        Assert.Equal(7, (await test.Db.Characters.AsNoTracking().SingleAsync()).TalentPoints);
        Assert.Equal(2, await test.Db.CharacterSkillTalents.CountAsync());
    }

    [Fact]
    public async Task RefundHealthRankClampsHealthAndPreservesOtherPassiveBonuses()
    {
        await using var test = await FormalTreeContext.CreateAsync("mage", 10, 9);
        await PurchasePathAsync(test, RootCodes("mage"));
        await PurchasePathAsync(test, "mage-ice-armor", "mage-ice-armor");
        test.Character.Hp = TalentRules.EffectiveMaxHp(test.Character);
        await test.Db.SaveChangesAsync();

        var (response, error) = await test.Service.RefundTalentNodeAsync("token", 1, "mage-ice-armor");

        Assert.Null(error);
        Assert.Equal(5, response!.TalentPoints);
        Assert.Equal(7, test.Character.TalentMaxHpPercent);
        Assert.Equal(107, test.Character.Hp);
        Assert.Equal(3, test.Character.TalentSkillDamagePercent);
        Assert.Equal(3, test.Character.TalentNormalAttackPercent);
    }

    [Fact]
    public async Task RefundMandatoryParentNamesTheDependentAndLeavesTheBuildUnchanged()
    {
        await using var test = await FormalTreeContext.CreateAsync("mage", 10, 9);
        await PurchasePathAsync(test, RootCodes("mage"));
        await PurchasePathAsync(test, "mage-barrage-talent", "mage-suppression-talent", "mage-precision", "mage-precision", "mage-arcane-mastery");
        var version = test.Character.Version;

        var (response, error) = await test.Service.RefundTalentNodeAsync("token", 1, "mage-barrage-talent");

        Assert.Null(response);
        Assert.StartsWith("TalentRefundBlocked:", error);
        Assert.Contains("奥术掌握", error);
        Assert.Equal(1, test.Character.TalentPoints);
        Assert.Equal(version, test.Character.Version);
        Assert.Equal(8, (await test.Db.CharacterSkillTalents.ToListAsync()).Sum(node => node.PointsSpent));
    }

    [Fact]
    public async Task RefundOptionalParentAcceptsAnotherMaxRankParentButRejectsTheLastOne()
    {
        await using var test = await FormalTreeContext.CreateAsync("mage", 10, 9);
        await PurchasePathAsync(test, RootCodes("mage"));
        await PurchasePathAsync(test, "mage-barrage-talent", "mage-precision", "mage-precision",
            "mage-countermagic", "mage-countermagic", "mage-arcane-mastery");

        var (response, error) = await test.Service.RefundTalentNodeAsync("token", 1, "mage-precision");

        Assert.Null(error);
        Assert.Equal(1, response!.TalentPoints);
        Assert.Equal(1, response.TalentNodes.Single(node => node.Code == "mage-precision").Rank);
        Assert.Equal(1, response.TalentNodes.Single(node => node.Code == "mage-arcane-mastery").Rank);
        var blocked = await test.Service.RefundTalentNodeAsync("token", 1, "mage-countermagic");
        Assert.Null(blocked.Response);
        Assert.Contains("奥术掌握", blocked.Error);
        Assert.Equal(2, (await test.Db.CharacterSkillTalents.SingleAsync(node => node.NodeCode == "mage-countermagic")).PointsSpent);
    }

    [Theory]
    [InlineData("mage-barrage-talent", 1, "奥术弹幕")]
    [InlineData("mage-ice-armor", 2, "冰甲术")]
    public async Task RefundCannotUseRetainedRanksToSupplyTheirOwnTreeEntryRequirement(string nodeCode, int rank, string name)
    {
        await using var test = await FormalTreeContext.CreateAsync("mage", 10, 9);
        await PurchasePathAsync(test, RootCodes("mage"));
        for (var i = 0; i < rank; i++) await PurchaseAvailableNodeAsync(test, nodeCode);
        var points = test.Character.TalentPoints;

        var blocked = await test.Service.RefundTalentNodeAsync("token", 1, "mage-flow");

        Assert.Null(blocked.Response);
        Assert.Contains("基础树投入不足", blocked.Error);
        Assert.Contains(name, blocked.Error);
        Assert.Equal(points, test.Character.TalentPoints);
        Assert.Equal(1, (await test.Db.CharacterSkillTalents.SingleAsync(node => node.NodeCode == "mage-flow")).PointsSpent);
    }

    [Fact]
    public async Task RefundSkillUnlockClearsOnlyItsEquipmentCooldownsAndQueuedAction()
    {
        await using var test = await FormalTreeContext.CreateAsync("acolyte", 10, 9);
        await PurchasePathAsync(test, "acolyte-prayer", "acolyte-echo", "acolyte-heal-training",
            "acolyte-purify-talent", "acolyte-silence-talent");
        Assert.Null((await test.Service.PromoteAsync("token", 1, new PromoteCharacterRequest { ProfessionCode = "priest" })).Error);
        Assert.Null((await test.Service.SetSlotAsync("token", 1, 4,
            new SetSkillSlotRequest { SkillCode = "acolyte-purify", AutoUseEnabled = true, AutoHpThresholdPercent = 75 })).Error);
        Assert.Null((await test.Service.SetSlotAsync("token", 1, 5,
            new SetSkillSlotRequest { SkillCode = "acolyte-silence", AutoUseEnabled = true, AutoHpThresholdPercent = 50 })).Error);
        var room = new Room { Id = 1, OwnerUserId = 1, SlotCount = 1, Status = RoomStatus.BattleOver, RoundNumber = 4 };
        var roomSlot = new RoomSlot { RoomId = 1, CharacterId = 1, SlotIndex = 1, UserId = 1,
            PendingSkillSlotMask = SkillRules.SlotMask(4) | SkillRules.SlotMask(5), IsSoulImprintQueued = true };
        test.Db.AddRange(room, roomSlot,
            new BattleSkillCooldown { RoomId = 1, CharacterId = 1, SkillCode = "acolyte-purify", ReadyAtRound = 9 },
            new BattleSkillCooldown { RoomId = 1, CharacterId = 1, SkillCode = "acolyte-silence", ReadyAtRound = 9 });
        await test.Db.SaveChangesAsync();

        var (response, error) = await test.Service.RefundTalentNodeAsync("token", 1, "acolyte-purify-talent");

        Assert.Null(error);
        Assert.Equal(5, response!.TalentPoints);
        Assert.DoesNotContain(response.LearnedSkills, skill => skill.Code == "acolyte-purify");
        Assert.Null(response.Slots.Single(slot => slot.SlotIndex == 4).SkillCode);
        Assert.False(response.Slots.Single(slot => slot.SlotIndex == 4).AutoUseEnabled);
        Assert.Equal("acolyte-silence", response.Slots.Single(slot => slot.SlotIndex == 5).SkillCode);
        Assert.True(response.Slots.Single(slot => slot.SlotIndex == 5).AutoUseEnabled);
        Assert.Contains(response.LearnedSkills, skill => skill.Code == "priest-group-heal");
        Assert.Contains(response.Slots, slot => slot.SkillCode == "priest-group-heal");
        Assert.Equal("acolyte-silence", (await test.Db.BattleSkillCooldowns.SingleAsync()).SkillCode);
        Assert.Equal(SkillRules.SlotMask(5), roomSlot.PendingSkillSlotMask);
        Assert.True(roomSlot.IsSoulImprintQueued);
        Assert.Equal(1, room.Version);
    }

    [Theory]
    [InlineData(RoomStatus.Preparing, 0)]
    [InlineData(RoomStatus.Cooldown, 1)]
    [InlineData(RoomStatus.NotStarted, 1)]
    public async Task RefundRespectsBattleLocks(RoomStatus status, int round)
    {
        await using var test = await FormalTreeContext.CreateAsync("swordsman", 10, 9);
        await PurchaseAvailableNodeAsync(test, "sword-edge");
        test.Db.AddRange(new Room { Id = 1, OwnerUserId = 1, SlotCount = 1, Status = status, RoundNumber = round },
            new RoomSlot { RoomId = 1, CharacterId = 1, SlotIndex = 1, UserId = 1 });
        await test.Db.SaveChangesAsync();

        Assert.Equal("LoadoutLocked", (await test.Service.RefundTalentNodeAsync("token", 1, "sword-edge")).Error);
        Assert.Equal(8, test.Character.TalentPoints);
        Assert.Equal(1, await test.Db.CharacterSkillTalents.CountAsync());
    }

    [Fact]
    public async Task RefundValidatesAuthenticationOwnershipAndProfession()
    {
        await using var test = await FormalTreeContext.CreateAsync("swordsman", 10, 9);
        await PurchaseAvailableNodeAsync(test, "sword-edge");
        Assert.Equal("Unauthorized", (await test.Service.RefundTalentNodeAsync(null, 1, "sword-edge")).Error);
        Assert.Equal("CharacterNotFound", (await test.Service.RefundTalentNodeAsync("token", 99, "sword-edge")).Error);
        Assert.Equal("InvalidSkillTalent", (await test.Service.RefundTalentNodeAsync("token", 1, "mage-flow")).Error);
        test.Character.UserId = 2;
        await test.Db.SaveChangesAsync();
        Assert.Equal("NotOwner", (await test.Service.RefundTalentNodeAsync("token", 1, "sword-edge")).Error);
        Assert.Equal(8, test.Character.TalentPoints);
    }

    [Fact]
    public async Task RefundConcurrencyConflictRollsBackTheRankAndPointTogether()
    {
        await using var test = await FormalTreeContext.CreateAsync("swordsman", 10, 9);
        await PurchaseAvailableNodeAsync(test, "sword-edge");
        await test.Db.Database.ExecuteSqlRawAsync("UPDATE Characters SET Version = Version + 1 WHERE Id = 1");

        Assert.Equal("ConcurrencyConflict", (await test.Service.RefundTalentNodeAsync("token", 1, "sword-edge")).Error);
        Assert.Equal(8, (await test.Db.Characters.AsNoTracking().SingleAsync()).TalentPoints);
        Assert.Equal(1, (await test.Db.CharacterSkillTalents.AsNoTracking().SingleAsync()).PointsSpent);
        var retry = await test.Service.RefundTalentNodeAsync("token", 1, "sword-edge");
        Assert.Null(retry.Error);
        Assert.Equal(9, retry.Response!.TalentPoints);
        Assert.Empty(await test.Db.CharacterSkillTalents.ToListAsync());
    }
}
