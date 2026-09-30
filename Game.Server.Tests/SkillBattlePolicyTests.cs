using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class SkillBattlePolicyTests
{
    [Fact]
    public void SelfHealAndSelectedHealUseTheirActualTargetsAndReturnUsefulReasons()
    {
        var state = Party();
        var self = Skill([new() { Type = "Heal", Target = "Self", Power = 10 }]);
        Assert.Equal("NoInjuredTarget", SkillBattlePolicy.Availability(self, state).UnavailableReason);
        var heal = Skill([new() { Type = "Heal", Target = "LowestHpAlly", Power = 10 }]);
        var availability = SkillBattlePolicy.Availability(heal, state);
        Assert.True(availability.CanUse);
        Assert.True(availability.CanChooseAllyTarget);
        Assert.Equal(new[] { 2, 3 }, availability.AllowedTargetCharacterIds);
        Assert.Equal(3, Assert.Single(SkillBattlePolicy.SelectAllies(heal.Effects[0], state)).Id);
        Assert.Equal(2, Assert.Single(SkillBattlePolicy.SelectAllies(heal.Effects[0], state, 2)).Id);
        Assert.False(SkillBattlePolicy.HasApplicableEffect(heal, state, 1));
        Assert.False(SkillBattlePolicy.HasApplicableEffect(heal, state, 4));
        Assert.False(SkillBattlePolicy.HasApplicableEffect(heal, state, 999));
    }

    [Fact]
    public void GroupScopeAndFixedTargetsRemainIndependentOfManualSelection()
    {
        var group = Skill([new() { Type = "Heal", Target = "AllAlive", Power = 10 },
            new() { Type = "Cleanse", Target = "FirstDebuffedAlly" }]);
        Assert.False(SkillBattlePolicy.CanChooseAllyTarget(group));
        Assert.Empty(SkillBattlePolicy.Availability(group, Party()).AllowedTargetCharacterIds);
        Assert.False(SkillBattlePolicy.HasApplicableEffect(group, Party(), 2));
        var fixedHeal = Skill([new() { Type = "Heal", Target = "LowestHpAllyFixed", Power = 10 }]);
        Assert.Equal(3, Assert.Single(SkillBattlePolicy.SelectAllies(fixedHeal.Effects[0], Party(), 2)).Id);
    }

    [Fact]
    public void ManualDamageInterruptAndAutoTimingHaveSeparateRules()
    {
        var mixed = Skill([new() { Type = "Damage", Target = "Monster", Power = 10 }, new() { Type = "Interrupt", Target = "Monster" }], "InterruptibleIntent");
        var state = Party() with { MonsterHasInterruptibleSkill = true };
        Assert.True(SkillBattlePolicy.Availability(mixed, state).CanUse);
        Assert.False(SkillBattlePolicy.MeetsAutoCondition(mixed, state, null, 70));
        Assert.True(SkillBattlePolicy.MeetsAutoCondition(mixed, state with { CanInterrupt = true }, null, 70));
        var pure = Skill([new() { Type = "Interrupt", Target = "Monster" }, new() { Type = "Guard", Target = "Self", Power = 10 }]);
        Assert.Equal("NoInterruptibleIntent", SkillBattlePolicy.Availability(pure, state).UnavailableReason);
    }

    [Fact]
    public void AutoHealthThresholdUsesInclusiveIntegerComparisonAndCorrectDefaultTarget()
    {
        var selfGuard = Skill([new() { Type = "Guard", Target = "Self", Power = 10 }]);
        Assert.False(SkillBattlePolicy.MeetsAutoCondition(selfGuard, Party(), null, 70));
        var snapshot = Party() with { Allies = [new(1, 1, 70, 100, false), new(2, 2, 69, 100, false)] };
        Assert.True(SkillBattlePolicy.MeetsAutoCondition(selfGuard, snapshot, null, 70));
        Assert.False(SkillBattlePolicy.MeetsAutoCondition(selfGuard, snapshot, null, 69));
        Assert.True(SkillBattlePolicy.MeetsAutoCondition(selfGuard, snapshot, "AllyHpBelowThreshold", 69));
    }

    private static SkillBattleSnapshot Party() => new(1,
        [new(1, 1, 100, 100, false), new(2, 2, 80, 100, false), new(3, 3, 20, 100, true), new(4, 4, 0, 100, true)],
        100, 100, true, false, false, false, []);

    private static CharacterSkillDefinition Skill(List<CombatSkillEffectOptions> effects, string? condition = null)
    {
        var catalog = new SkillCatalog(Options.Create(new SkillOptions
        {
            Professions = [new() { Code = "test", Name = "Test", StartingSkills = ["test"] }],
            Abilities = [new() { Code = "test", ProfessionCode = "test", Name = "Test", Description = "Test", Effects = effects, AutoCondition = condition ?? string.Empty }]
        }));
        return catalog.Resolve(new Character { ProfessionCode = "test", Level = 1 }, "test")!;
    }
}

public partial class BattleServiceTests
{
    [Fact]
    public async Task RoomSkillCapabilitiesAgreeWithQueueValidationAndRefreshWhenTargetIsHealed()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 100);
        test.Character.ProfessionCode = "cleric";
        var target = await test.AddSlotAsync(2, "Injured", hp: 50);
        await test.AddSkillAsync(test.Character, 1, "cleric-heal", autoUse: false);
        var skill = (await test.GetRoomDetailAsync())!.Slots[0].Skills[0];
        Assert.True(skill.CanUse);
        Assert.Null(skill.UnavailableReason);
        Assert.True(skill.CanChooseAllyTarget);
        Assert.Equal(new[] { target.Id }, skill.AllowedTargetCharacterIds);
        var request = new QueueSkillRequest { RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 1,
            IsQueued = true, TargetCharacterId = target.Id };
        Assert.True((await test.Service.QueueSkillAsync(request, test.Token)).Success);
        target.Hp = target.MaxHp;
        await test.Db.SaveChangesAsync();
        var updated = (await test.GetRoomDetailAsync())!.Slots[0].Skills[0];
        Assert.False(updated.CanUse);
        Assert.Equal("NoInjuredTarget", updated.UnavailableReason);
        Assert.Empty(updated.AllowedTargetCharacterIds);
        Assert.Equal("NoValidSkillTarget", (await test.Service.QueueSkillAsync(request, test.Token)).Error);
        Assert.True((await test.Service.QueueSkillAsync(new QueueSkillRequest { RoomId = test.Room.Id,
            CharacterId = test.Character.Id, SkillSlotIndex = 1, IsQueued = false }, test.Token)).Success);
    }
}
