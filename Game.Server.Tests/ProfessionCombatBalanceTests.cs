using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Fact]
    public async Task FaithBarrierProtectsKnightMoreThanOtherAllyAgainstAreaAttack()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 60, characterAttack: 1, monsterAttack: 20);
        test.Character.ProfessionCode = "swordsman";
        test.Character.Level = 3;
        var ally = await test.AddSlotAsync(2, "Ally", hp: 60, attack: 1);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Monster.CombatProfileCode = "balance-area-profile";
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "knight-faith-barrier", autoUse: true);
        var (service, _) = CreateProfessionBalanceService(test);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(48, test.Character.Hp); // 20 damage reduced by 40%, not 40% plus the party's 10%.
        Assert.Equal(42, ally.Hp); // 20 damage reduced by 10%.
        Assert.Equal(2, result!.Logs.Count(log => log.Contains("使用 信仰壁垒，守护")));
        Assert.Single(result.Logs, log => log.Contains("Knight 守护反击"));
    }

    [Theory]
    [InlineData(100, 50)]
    [InlineData(50, 100)]
    public async Task RearKnightAutomaticBarrierProtectsBothPositionsRegardlessOfHealth(
        int frontHp, int knightHp)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: frontHp, characterAttack: 1, monsterAttack: 20);
        var knight = await test.AddSlotAsync(2, "RearKnight", hp: knightHp, attack: 1);
        knight.ProfessionCode = "swordsman";
        knight.Level = 3;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Monster.CombatProfileCode = "balance-area-profile";
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(knight, 1, "knight-faith-barrier", autoUse: true);
        var (service, _) = CreateProfessionBalanceService(test);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(frontHp - 18, test.Character.Hp);
        Assert.Equal(knightHp - 12, knight.Hp);
        Assert.Equal(2, result!.Logs.Count(log => log.Contains("RearKnight 使用 信仰壁垒，守护")));
        Assert.Single(result.Logs, log => log.Contains("RearKnight 守护反击"));
    }

    [Theory]
    [InlineData(75, "soul-frost-guard", 0, 100)]
    [InlineData(75, null, 0, 250)]
    [InlineData(0, "soul-frost-guard", 0, 500)]
    [InlineData(75, "warrior-fury-risk", 0, 400)]
    [InlineData(75, null, 15, 400)]
    [InlineData(0, "warrior-fury-risk", 15, 1300)]
    public async Task TotalPlayerReductionHasACapWithoutRemovingDamageTakenPenalties(
        int guardPercent, string? statusCode, int potionDamageTakenPercent, int expectedDamage)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1000);
        test.Character.Hp = test.Character.MaxHp = 2000;
        await test.Db.SaveChangesAsync();
        var (_, monsterCombat) = CreateProfessionBalanceService(test);
        if (statusCode is not null)
            await monsterCombat.ApplyStatusAsync(test.Room, "Character", test.Character.Id,
                statusCode, 1, [], test.Character.Name);
        await test.Db.SaveChangesAsync();
        var slot = await test.Db.RoomSlots.SingleAsync();
        await new BattleGuardService(monsterCombat.Statuses).ApplyAsync(test.Room, test.Character.Id,
            guardPercent, new("Character", test.Character.Id), false);

        await monsterCombat.ExecuteIntentAsync(test.Room, test.Monster,
            [new BattleParticipant(slot, test.Character)], new Dictionary<int, Game.Shared.Enums.ElementType>(),
            [],
            new Dictionary<int, OperationPotionBonuses>
            {
                [test.Character.Id] = new(0, 0, potionDamageTakenPercent, 0, 0)
            });

        Assert.Equal(2000 - expectedDamage, test.Character.Hp);
    }

    private static (BattleService Service, MonsterCombatService MonsterCombat) CreateProfessionBalanceService(
        BattleTestContext test)
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();
        var monsterOptions = configuration.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!;
        monsterOptions.Skills.Add(new MonsterSkillOptions
        {
            Code = "balance-area", Name = "全体攻击", Description = "防护机制验证", TargetType = "AllAlive",
            DamagePowerPercent = 100
        });
        monsterOptions.Profiles["balance-area-profile"] = new MonsterCombatProfileOptions
        {
            SkillUseChancePercent = 100, Skills = [new() { Code = "balance-area" }]
        };
        var monsterCatalog = new MonsterCombatCatalog(Options.Create(monsterOptions));
        var monsterCombat = new MonsterCombatService(test.Db, monsterCatalog);
        var skillOptions = configuration.GetSection(SkillOptions.SectionName).Get<SkillOptions>()!;
        foreach (var skill in skillOptions.Abilities)
        {
            skill.UnlockLevel = 1;
            skill.InitialCooldownRounds = 0;
        }
        var skills = new SkillCatalog(Options.Create(skillOptions), monsterCatalog);
        var progression = ProgressionTestFactory.Create();
        var rewards = RewardTestFactory.CreateService(test.Db, progression);
        return (new BattleService(test.Db, new UserService(test.Db, progression, skills),
            ConsumableTestFactory.Create(), skills, rewards,
            new DungeonRunService(test.Db, rewards, monsterCombat), monsterCombat), monsterCombat);
    }
}
