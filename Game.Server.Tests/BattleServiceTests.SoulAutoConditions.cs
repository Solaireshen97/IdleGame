using Game.Server.Services;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Dtos.Characters;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Theory]
    [InlineData("Always", 100, 100, true)]
    [InlineData("SelfHpBelowThreshold", 45, 100, true)]
    [InlineData("SelfHpBelowThreshold", 46, 40, false)]
    [InlineData("AllyHpBelowThreshold", 100, 45, true)]
    [InlineData("AllyHpBelowThreshold", 100, 46, false)]
    [InlineData("FrontAllyHpBelowThreshold", 45, 100, true)]
    [InlineData("FrontAllyHpBelowThreshold", 46, 40, false)]
    [InlineData("MonsterHpBelowThreshold", 100, 100, false)]
    [InlineData("AllyHasDebuff", 100, 100, false)]
    [InlineData("MonsterHasBuff", 100, 100, false)]
    [InlineData("InterruptibleIntent", 100, 100, false)]
    [InlineData("PreferInterrupt", 100, 100, true)]
    public async Task SoulAutoUsesTheSameConditionsAsSkills(string condition, int hp, int allyHp, bool casts)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: hp, monsterAttack: 1);
        await test.AddSlotAsync(2, "队友", hp: allyHp);
        var (service, _) = CreateProductionSoulBattleService(test);
        await EquipBalancedSoulAsync(test, test.Character, "plague-widow-essence", condition, 45);
        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(error);
        Assert.Equal(casts, result!.Events.Any(e => e.SkillCode == "plague-widow-essence" && e.Kind == BattleEventKind.Damage));
        Assert.Equal(casts, await test.Db.BattleSkillCooldowns.AnyAsync(c => c.SkillCode == SoulImprintRules.CooldownCode("plague-widow-essence")));
    }

    [Theory]
    [InlineData("AllyHasDebuff", "Character", "poison")]
    [InlineData("InterruptibleIntent", "Intent", "goldtooth-smash")]
    public async Task SoulAutoRespondsToStatusesAndCurrentIntent(string condition, string targetType, string code)
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        var (service, combat) = CreateProductionSoulBattleService(test);
        await EquipBalancedSoulAsync(test, test.Character, "plague-widow-essence", condition);
        if (targetType == "Intent")
            test.Db.MonsterIntents.Add(new Game.Shared.Models.MonsterIntent
            {
                RoomId = test.Room.Id, RunSequence = test.Room.RunSequence, RoundNumber = test.Room.RoundNumber,
                MonsterId = test.Monster.Id, ActionType = "Skill", SkillCode = code,
                TargetType = "Front", TargetCharacterId = test.Character.Id
            });
        else
            await combat.ApplyStatusAsync(test.Room, targetType, test.Character.Id, code, 3, [], test.Character.Name);
        await test.Db.SaveChangesAsync();
        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(error);
        Assert.Contains(result!.Events, e => e.SkillCode == "plague-widow-essence" && e.Kind == BattleEventKind.Damage);
    }

    [Fact]
    public async Task ManualSoulBypassesConfiguredCondition()
    {
        await using var test = await BattleTestContext.CreateAsync(monsterAttack: 1);
        var (service, _) = CreateProductionSoulBattleService(test);
        await EquipBalancedSoulAsync(test, test.Character, "plague-widow-essence", "SelfHpBelowThreshold", 1);
        var slot = await test.Db.RoomSlots.SingleAsync();
        slot.IsSoulImprintQueued = true;
        await test.Db.SaveChangesAsync();
        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(error);
        Assert.Contains(result!.Events, e => e.SkillCode == "plague-widow-essence" && e.Kind == BattleEventKind.Damage);
    }

    [Fact]
    public async Task RoomSoulConditionIsPersistedProjectedAndUsedWithoutChangingBase()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 50, monsterAttack: 1);
        await EquipBalancedSoulAsync(test, test.Character, "deep-core");
        var imprint = await test.Db.CharacterSoulImprints.SingleAsync();
        var service = new SoulImprintService(test.Db,
            new UserService(test.Db, ProgressionTestFactory.Create(), SkillTestFactory.Create()), SoulImprintTestFactory.Create());
        var (response, error) = await service.SetAutoAsync(test.Token, test.Character.Id, imprint.Id,
            new() { AutoUseEnabled = true, AutoConditionOverride = "SelfHpBelowThreshold", AutoHpThresholdPercent = 40 });
        Assert.Null(error);
        Assert.Equal("SelfHpBelowThreshold", Assert.Single(response!.SoulImprints).AutoCondition);
        Assert.Null(imprint.AutoConditionOverride);
        Assert.Equal(70, imprint.AutoHpThresholdPercent);
        await using var restored = test.CreateDbContext();
        var policy = BattleAutoPolicyResolver.Soul(await restored.RoomSlots.SingleAsync(), await restored.CharacterSoulImprints.SingleAsync());
        Assert.Equal((true, "SelfHpBelowThreshold", 40), (policy.AutoUseEnabled, policy.AutoConditionOverride, policy.AutoHpThresholdPercent));
        var detail = await test.GetRoomDetailAsync();
        var projected = Assert.Single(detail!.Slots).SoulImprint!;
        Assert.Equal(("SelfHpBelowThreshold", 40), (projected.AutoCondition, projected.AutoHpThresholdPercent));
        var (battle, battleError) = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(battleError);
        Assert.DoesNotContain(battle!.Logs, log => log.Contains("释放魂印"));
    }
}
