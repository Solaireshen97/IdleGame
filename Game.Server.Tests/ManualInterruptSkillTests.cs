using Game.Server.Configuration;
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
    [Theory]
    [InlineData("knight-rebuke", "basic")]
    [InlineData("knight-rebuke", "uninterruptible")]
    [InlineData("knight-rebuke", "interrupted")]
    public async Task ManualDamageInterruptSkillsDamageWithoutInterruptibleIntent(string skillCode, string intentState)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 10, monsterAttack: 1, monsterDefense: 0);
        var (service, _, skill, intent) = await PrepareInterruptSkillAsync(test, skillCode, intentState);
        Assert.False(SkillRules.RequiresInterruptibleTarget(SkillCatalog.EffectsFor(skill).Select(effect => effect.Type)));

        var (queued, queueError) = await service.QueueSkillAsync(new QueueSkillRequest
            { RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 1, IsQueued = true }, test.Token);
        Assert.Null(queueError);
        Assert.True(queued);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(SkillCatalog.EffectsFor(skill).Count(effect => effect.Type == "Damage"),
            result!.Logs.Count(log => log.Contains($"使用 {skill.Name} 攻击")));
        Assert.DoesNotContain(result.Logs, log => log.Contains($"使用 {skill.Name}，打断"));
        var cooldown = Assert.Single(await test.Db.BattleSkillCooldowns.ToListAsync());
        Assert.Equal(skillCode, cooldown.SkillCode);
        Assert.Equal(intent.RoundNumber + skill.CooldownRounds + 1, cooldown.ReadyAtRound);
        Assert.Equal(intentState == "interrupted", intent.IsInterrupted);
    }

    [Theory]
    [InlineData("knight-rebuke")]
    public async Task ManualDamageInterruptSkillsStillDamageWhenIntentIsInterruptedAfterQueue(string skillCode)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 10, monsterAttack: 1, monsterDefense: 0);
        var (service, monsterCombat, skill, _) = await PrepareInterruptSkillAsync(test, skillCode, "interruptible");
        var (queued, queueError) = await service.QueueSkillAsync(new QueueSkillRequest
            { RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 1, IsQueued = true }, test.Token);
        Assert.Null(queueError);
        Assert.True(queued);
        Assert.True(await monsterCombat.InterruptCurrentIntentAsync(test.Room, test.Monster));
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(SkillCatalog.EffectsFor(skill).Count(effect => effect.Type == "Damage"),
            result!.Logs.Count(log => log.Contains($"使用 {skill.Name} 攻击")));
        Assert.DoesNotContain(result.Logs, log => log.Contains($"使用 {skill.Name}，打断"));
        Assert.Equal(skillCode, Assert.Single(await test.Db.BattleSkillCooldowns.ToListAsync()).SkillCode);
    }

    [Theory]
    [InlineData("knight-rebuke", "basic", true)]
    [InlineData("knight-rebuke", "uninterruptible", true)]
    [InlineData("knight-rebuke", "interrupted", false)]
    [InlineData("knight-rebuke", "interruptible", true)]
    public async Task AutomaticDamageInterruptSkillsStillWaitForInterruptibleIntent(string skillCode, string intentState, bool shouldCast)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 10, monsterAttack: 1, monsterDefense: 0);
        var (service, _, skill, intent) = await PrepareInterruptSkillAsync(test, skillCode, intentState, autoUse: true);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(shouldCast ? SkillCatalog.EffectsFor(skill).Count(effect => effect.Type == "Damage") : 0,
            result!.Logs.Count(log => log.Contains($"使用 {skill.Name} 攻击")));
        Assert.Equal(shouldCast && intentState == "interruptible" ? 1 : 0,
            result.Logs.Count(log => log.Contains($"使用 {skill.Name}，打断")));
        Assert.Equal(shouldCast && intentState == "interruptible" || intentState == "interrupted", intent.IsInterrupted);
        var cooldowns = await test.Db.BattleSkillCooldowns.ToListAsync();
        if (shouldCast) Assert.Equal(skillCode, Assert.Single(cooldowns).SkillCode);
        else Assert.Empty(cooldowns);
    }

    private static async Task<(BattleService Service, MonsterCombatService MonsterCombat, CombatSkillOptions Skill, MonsterIntent Intent)>
        PrepareInterruptSkillAsync(BattleTestContext test, string skillCode, string intentState, bool autoUse = false)
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();
        var monsterOptions = configuration.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!;
        monsterOptions.Skills.Add(new MonsterSkillOptions
        {
            Code = "manual-interrupt-probe", Name = "测试攻击", Description = "验证手动打断技能。",
            DamagePowerPercent = 100, IsInterruptible = intentState != "uninterruptible"
        });
        monsterOptions.Profiles["manual-interrupt-probe"] = new MonsterCombatProfileOptions
        {
            SkillUseChancePercent = intentState == "basic" ? 0 : 100,
            Skills = [new MonsterProfileSkillOptions { Code = "manual-interrupt-probe" }]
        };
        var monsters = new MonsterCombatCatalog(Options.Create(monsterOptions));
        var monsterCombat = new MonsterCombatService(test.Db, monsters);
        var skills = CreateImmediateProductionSkills(configuration, monsters);
        var skill = skills.FindSkill(skillCode)!;
        test.Character.ProfessionCode = skill.ProfessionCode;
        test.Character.Level = 10;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Monster.CombatProfileCode = "manual-interrupt-probe";
        await test.Db.SaveChangesAsync();

        var progression = ProgressionTestFactory.Create();
        var userService = new UserService(test.Db, progression, skills);
        await test.AddSkillAsync(test.Character, 1, skillCode, autoUse);

        var intent = await monsterCombat.EnsureIntentAsync(test.Room, test.Monster);
        Assert.Equal(intentState == "basic" ? "BasicAttack" : "Skill", intent.ActionType);
        if (intentState == "interrupted") intent.IsInterrupted = true;
        await test.Db.SaveChangesAsync();
        var rewards = RewardTestFactory.CreateService(test.Db, progression);
        var service = new BattleService(test.Db, userService, ConsumableTestFactory.Create(), skills, rewards,
            new DungeonRunService(test.Db, rewards, monsterCombat), monsterCombat);
        return (service, monsterCombat, skill, intent);
    }
}
