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
    public async Task FrontGuardAndBacklineSelfDefenceProtectTheirOwnTargetsAgainstAreaDamage()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 60, characterAttack: 1, monsterAttack: 20);
        test.Character.ProfessionCode = "swordsman";
        test.Character.AdvancedProfessionCode = "knight";
        test.Character.Level = 10;
        var mage = await test.AddSlotAsync(2, "Mage", hp: 60, attack: 1);
        var rogue = await test.AddSlotAsync(3, "Rogue", hp: 60, attack: 1);
        mage.ProfessionCode = "mage";
        rogue.ProfessionCode = "rogue";
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Monster.CombatProfileCode = "balance-area-profile";
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "knight-guard", autoUse: true);
        await test.AddSkillAsync(mage, 1, "mage-frost-ward", autoUse: true);
        await test.AddSkillAsync(rogue, 1, "rogue-evasion", autoUse: true);
        var (service, _) = CreateProfessionBalanceService(test);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(55, test.Character.Hp);
        Assert.Equal(46, mage.Hp);
        Assert.Equal(46, rogue.Hp);
        Assert.Equal(3, await test.Db.BattleSkillCooldowns.CountAsync());
    }

    [Fact]
    public async Task BacklineSelfDefenceDoesNotStealOrStrengthenFrontParry()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 60, characterAttack: 1, monsterAttack: 20);
        test.Character.ProfessionCode = "swordsman";
        var mage = await test.AddSlotAsync(2, "Mage", hp: 60, attack: 1);
        mage.ProfessionCode = "mage";
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "sword-parry", autoUse: true);
        await test.AddSkillAsync(mage, 1, "mage-frost-ward", autoUse: true);
        var (service, _) = CreateProfessionBalanceService(test);

        var (_, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(47, test.Character.Hp);
        Assert.Equal(60, mage.Hp);
    }

    [Fact]
    public async Task StrongerKnightGuardCanReplaceAnEarlierAutomaticParry()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 60, characterAttack: 1, monsterAttack: 20);
        test.Character.ProfessionCode = "swordsman";
        test.Character.AdvancedProfessionCode = "knight";
        test.Character.Level = 10;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "sword-parry", autoUse: true);
        await test.AddSkillAsync(test.Character, 2, "knight-guard", autoUse: true);
        var (service, _) = CreateProfessionBalanceService(test);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(55, test.Character.Hp);
        Assert.Contains(result!.Logs, log => log.Contains("使用 守护"));
    }

    [Theory]
    [InlineData(100, 50, true)]
    [InlineData(50, 100, false)]
    public async Task RearSwordsmanAutomaticParryChecksItsOwnHealth(
        int frontHp, int swordsmanHp, bool shouldParry)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: frontHp, characterAttack: 1, monsterAttack: 1);
        var swordsman = await test.AddSlotAsync(2, "RearSwordsman", hp: swordsmanHp, attack: 1);
        swordsman.ProfessionCode = "swordsman";
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(swordsman, 1, "sword-parry", autoUse: true);
        var (service, _) = CreateProfessionBalanceService(test);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(shouldParry, result!.Logs.Any(log => log.Contains("RearSwordsman 使用 招架")));
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

        await monsterCombat.ExecuteIntentAsync(test.Room, test.Monster,
            [new MonsterCombatParticipant(slot, test.Character)], new Dictionary<int, Game.Shared.Enums.ElementType>(),
            new PlayerRoundDefense(guardPercent, test.Character.Id, test.Character.Id), [],
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
        var skills = new SkillCatalog(Options.Create(
            configuration.GetSection(SkillOptions.SectionName).Get<SkillOptions>()!), monsterCatalog);
        var progression = ProgressionTestFactory.Create();
        var rewards = RewardTestFactory.CreateService(test.Db, progression);
        return (new BattleService(test.Db, new UserService(test.Db, progression, skills),
            ConsumableTestFactory.Create(), skills, rewards,
            new DungeonRunService(test.Db, rewards, monsterCombat), monsterCombat), monsterCombat);
    }
}
