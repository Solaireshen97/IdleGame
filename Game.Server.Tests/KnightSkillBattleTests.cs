using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Fact]
    public async Task InvigorateHealsSoloKnightOnlyOnceWhenKnightIsAlsoLowestHealthAlly()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 50, characterAttack: 1, monsterAttack: 1);
        test.Character.ProfessionCode = "swordsman";
        test.Character.Level = 7;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "knight-invigorate", autoUse: true);
        var (service, _) = CreateProductionSoulBattleService(test);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Single(result!.Logs, log => log.Contains("使用 振奋精神，为 1号位") && log.Contains("恢复 8 点"));
        Assert.Equal(57, test.Character.Hp);
        Assert.Equal("knight-invigorate", Assert.Single(await test.Db.BattleSkillCooldowns.ToListAsync()).SkillCode);
    }

    [Fact]
    public async Task InvigorateHealsKnightAndLowestHealthAllyOnceEach()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 50, characterAttack: 1, monsterAttack: 1);
        test.Character.ProfessionCode = "swordsman";
        test.Character.Level = 7;
        var ally = await test.AddSlotAsync(2, "Ally", hp: 40, attack: 1);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "knight-invigorate", autoUse: true);
        var (service, _) = CreateProductionSoulBattleService(test);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(2, result!.Logs.Count(log => log.Contains("使用 振奋精神，为")));
        Assert.Equal(57, test.Character.Hp);
        Assert.Equal(48, ally.Hp);
    }

    [Fact]
    public async Task HolyAuraAppliesSeparateDamageAndHealingStatusesToParty()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 50, characterAttack: 20, monsterAttack: 1);
        test.Character.ProfessionCode = "swordsman";
        test.Character.Level = 10;
        var ally = await test.AddSlotAsync(2, "Ally", hp: 50, attack: 1);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "knight-holy-aura", autoUse: true);
        var (service, _) = CreateProductionSoulBattleService(test);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("Slime 获得 圣焰"));
        Assert.Equal("knight-holy-aura", Assert.Single(await test.Db.BattleSkillCooldowns.ToListAsync()).SkillCode);
        var statuses = await test.Db.BattleStatusEffects.ToListAsync();
        Assert.Contains(statuses, status => status.TargetType == "Monster" && status.TargetId == test.Monster.Id &&
            status.EffectCode == "knight-holy-burn" && status.PerTickValue == 4);
        Assert.Equal(2, statuses.Count(status => status.TargetType == "Character" &&
            status.EffectCode == "knight-holy-renew" && status.PerTickValue == 4));
        Assert.Contains(statuses, status => status.TargetId == ally.Id && status.EffectCode == "knight-holy-renew");

        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        test.Character.Attack = 40; // Existing aura damage stays at the cast-time value.
        await test.Db.SaveChangesAsync();
        var (second, secondError) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(secondError);
        Assert.Single(second!.Logs, log => log.Contains("受到 圣焰 造成的 4 点伤害"));
        Assert.Equal(2, second.Logs.Count(log => log.Contains("受到 圣佑回春 治疗，恢复 4 点生命")));
    }

    [Fact]
    public async Task HolyAuraHealingTicksWhenThePartyDefeatsTheMonsterThisRound()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 50, characterAttack: 100, monsterAttack: 1);
        await test.EnableAutoForCharacterAsync(test.Character);
        test.Monster.Hp = 1;
        test.Room.RoundNumber = 3;
        var (service, monsterCombat) = CreateProductionSoulBattleService(test);
        await monsterCombat.ApplyStatusAsync(test.Room, "Character", test.Character.Id,
            "knight-holy-renew", 3, [], test.Character.Name, perTickValue: 4);
        test.Room.RoundNumber = 4;
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("受到 圣佑回春 治疗，恢复 4 点生命"));
        Assert.Equal(54, test.Character.Hp);
    }
}
