using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Theory]
    [InlineData(true, "AllAlive")]
    [InlineData(false, "AllAlive")]
    [InlineData(false, "AllOtherAlive")]
    [InlineData(false, "FirstDebuffedAlly")]
    public async Task RemovalTargetScopeHonorsPerTargetCountEligibilityAndEventOrder(bool monsterCast, string targetCode)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var protectedAlly = await test.AddSlotAsync(2, "Protected");
        var first = await test.AddSlotAsync(3, "First");
        var empty = await test.AddSlotAsync(4, "Empty");
        var second = await test.AddSlotAsync(5, "Second");
        var dead = await test.AddSlotAsync(6, "Dead", hp: 0);
        var positive = monsterCast;
        var statuses = new BattleStatusService(test.Db, new BattleStatusCatalog(Options.Create(new MonsterCombatOptions
        {
            StatusEffects =
            [
                new() { Code = "locked", Name = "Locked", Description = "Cannot remove", EffectType = "None", IsPositive = positive, IsDispellable = false },
                new() { Code = "first", Name = "First", Description = "First removal", EffectType = "None", IsPositive = positive },
                new() { Code = "second", Name = "Second", Description = "Must remain", EffectType = "None", IsPositive = positive },
                new() { Code = "opposite", Name = "Opposite", Description = "Wrong polarity", EffectType = "None", IsPositive = !positive }
            ]
        })));
        foreach (var character in new[] { test.Character, protectedAlly })
            await statuses.ApplyAsync(test.Room, "Character", character.Id, "locked", 3, [], character.Name);
        foreach (var character in new[] { first, second, dead })
        {
            await statuses.ApplyAsync(test.Room, "Character", character.Id, "opposite", 3, [], character.Name);
            await statuses.ApplyAsync(test.Room, "Character", character.Id, "locked", 3, [], character.Name);
            await statuses.ApplyAsync(test.Room, "Character", character.Id, "first", 3, [], character.Name);
            await statuses.ApplyAsync(test.Room, "Character", character.Id, "second", 3, [], character.Name);
        }
        // A removable status on the caster makes AllOtherAlive's exclusion observable.
        if (targetCode == "AllOtherAlive")
            await statuses.ApplyAsync(test.Room, "Character", test.Character.Id, "first", 3, [], test.Character.Name);
        await test.Db.SaveChangesAsync();

        var kind = monsterCast ? BattleEffectKind.Dispel : BattleEffectKind.Cleanse;
        BattleSkillDefinition skill;
        if (monsterCast)
            skill = new MonsterSkillDefinition
            {
                Code = "scope", Name = "Scope", Description = "Scope test", CooldownRounds = 0, InitialCooldownRounds = 0,
                TargetType = targetCode, DangerLevel = "Normal",
                Effects = [new(kind, new(BattleTargetSide.Opponent, BattleTargetSelection.AllAlive, false, false, targetCode))]
            };
        else
        {
            var selection = targetCode switch
            {
                "AllAlive" => BattleTargetSelection.AllAlive,
                "AllOtherAlive" => BattleTargetSelection.AllOtherAlive,
                _ => BattleTargetSelection.FirstDebuffed
            };
            skill = new CharacterSkillDefinition
            {
                Code = "scope", Name = "Scope", Description = "Scope test", CooldownRounds = 0, InitialCooldownRounds = 0,
                ProfessionCode = "scope", Level = 1, IsShared = false, UnlockLevel = 1, Level2UnlockLevel = 20,
                Level3UnlockLevel = 30, AutoCondition = "AllyHasDebuff",
                Effects = [new(kind, new(BattleTargetSide.Ally, selection, targetCode == "FirstDebuffedAlly", false, targetCode))]
            };
        }
        var party = await UnifiedPartyAsync(test.Db);
        var battle = new BattleExecutionContext(test.Room, test.Monster, party,
            new Dictionary<int, ElementType>(), new Dictionary<int, OperationPotionBonuses>(), []);
        var executor = UnifiedExecutor(statuses);
        using var recording = executor.Events.Begin(test.Room, test.Monster, party);
        var result = await executor.ExecuteAsync(new(battle, skill, monsterCast ? battle.Enemy : battle.Characters[0]));
        await test.Db.SaveChangesAsync();

        var expectedIds = targetCode == "FirstDebuffedAlly" ? new[] { first.Id } : new[] { first.Id, second.Id };
        Assert.Equal(expectedIds, result.Outcomes.Select(outcome => outcome.Target.Id));
        Assert.All(result.Outcomes, outcome =>
        {
            Assert.True(outcome.Applied);
            Assert.Equal(kind, outcome.Kind);
            Assert.Equal("first", outcome.RemovedStatus!.Code);
        });
        var events = executor.Events.Snapshot(test.Room);
        Assert.Equal(expectedIds, events.Select(item => item.Target!.ActorId));
        Assert.Equal(Enumerable.Range(1, expectedIds.Length), events.Select(item => item.Sequence));
        Assert.All(events, item =>
        {
            Assert.Equal(monsterCast ? BattleEventKind.Dispel : BattleEventKind.Cleanse, item.Kind);
            Assert.Equal("Character", item.Target!.ActorType);
            Assert.Equal("first", item.Status!.Code);
            Assert.Equal("scope", item.SkillCode);
            Assert.Equal(monsterCast ? "Monster" : "Character", item.Source!.ActorType);
            Assert.Equal(monsterCast ? test.Monster.Id : test.Character.Id, item.Source.ActorId);
        });
        foreach (var character in new[] { first, second, dead })
        {
            Assert.True(await statuses.HasAsync(test.Room, "Character", character.Id, "locked"));
            Assert.True(await statuses.HasAsync(test.Room, "Character", character.Id, "opposite"));
            Assert.True(await statuses.HasAsync(test.Room, "Character", character.Id, "second"));
            Assert.Equal(!expectedIds.Contains(character.Id), await statuses.HasAsync(test.Room, "Character", character.Id, "first"));
        }
        Assert.True(await statuses.HasAsync(test.Room, "Character", protectedAlly.Id, "locked"));
        Assert.Empty(await statuses.GetActiveAsync(test.Room, "Character", [empty.Id]));
        if (targetCode == "AllOtherAlive")
            Assert.True(await statuses.HasAsync(test.Room, "Character", test.Character.Id, "first"));
    }
}

