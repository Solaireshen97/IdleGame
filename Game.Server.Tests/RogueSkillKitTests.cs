using Game.Server.Configuration;
using Game.Server.Services;
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
    public void RogueCatalogUnlocksExactlyFiveSkillsWithTheIntendedRanksAndCooldowns()
    {
        var catalog = LoadRogueCatalog();
        var rogue = catalog.FindProfession("rogue")!;
        Assert.Equal(new[] { "rogue-shadow-strike" }, rogue.StartingSkills);
        Assert.Equal("rogue-adrenaline", rogue.SharedSkillCode);

        var expected = new[]
        {
            (Code: "rogue-shadow-strike", Unlock: 1, Rank2: 12, Rank3: 22, Initial: 1, Cooldown: 2),
            (Code: "rogue-execution-slash", Unlock: 3, Rank2: 14, Rank3: 24, Initial: 2, Cooldown: 4),
            (Code: "rogue-poisoned-blade", Unlock: 5, Rank2: 16, Rank3: 26, Initial: 1, Cooldown: 4),
            (Code: "rogue-adrenaline", Unlock: 7, Rank2: 18, Rank3: 28, Initial: 2, Cooldown: 4),
            (Code: "rogue-blade-flurry", Unlock: 10, Rank2: 20, Rank3: 30, Initial: 3, Cooldown: 6)
        };
        foreach (var item in expected)
        {
            var skill = catalog.FindSkill(item.Code)!;
            Assert.Equal((item.Unlock, item.Rank2, item.Rank3),
                (skill.UnlockLevel, skill.Level2UnlockLevel, skill.Level3UnlockLevel));
            Assert.Equal((item.Initial, item.Cooldown), (skill.InitialCooldownRounds, skill.CooldownRounds));
            Assert.Equal(1, SkillCatalog.RankFor(skill, item.Rank2 - 1));
            Assert.Equal(2, SkillCatalog.RankFor(skill, item.Rank2));
            Assert.Equal(3, SkillCatalog.RankFor(skill, item.Rank3));
            Assert.DoesNotContain(catalog.SkillsForProfessionAtLevel("rogue", item.Unlock - 1),
                candidate => candidate.Code == item.Code);
        }
        foreach (var level in new[] { 1, 3, 5, 7, 10 })
            Assert.Equal(expected.Count(item => item.Unlock <= level), catalog.SkillsForProfessionAtLevel("rogue", level).Count);
        Assert.Equal(expected.Select(item => item.Code).Order(),
            catalog.SkillsForProfessionAtLevel("rogue", 30).Select(skill => skill.Code).Order());

        Assert.Equal(new[] { 3, 3, 4 }, new[] { 10, 20, 30 }
            .Select(level => SkillCatalog.EffectsFor(catalog.SkillsForProfessionAtLevel("rogue", level)
                .Single(skill => skill.Code == "rogue-blade-flurry")).Count(effect => effect.Type == "Damage")));
        Assert.Equal(new[] { 80m, 90m, 100m }, new[] { 3, 14, 24 }
            .Select(level => SkillCatalog.EffectsFor(catalog.SkillsForProfessionAtLevel("rogue", level)
                .Single(skill => skill.Code == "rogue-execution-slash")).Single(effect => effect.Type == "Damage").AttackPowerPercent));
        var recipient = new Character { ProfessionCode = "mage", Level = 10 };
        var shared = catalog.ResolveSkillForLevel(recipient, "rogue-adrenaline",
            new Dictionary<string, int> { ["rogue"] = 30 })!;
        Assert.Equal(6, shared.CooldownRounds);
        Assert.Equal(2, shared.InitialCooldownRounds);
        Assert.Equal("rogue-adrenaline-3", Assert.Single(SkillCatalog.EffectsFor(shared)).StatusCode);
        var highLevelRecipient = new Character { ProfessionCode = "mage", Level = 30 };
        var highLevelShared = catalog.ResolveSkillForLevel(highLevelRecipient, "rogue-adrenaline",
            new Dictionary<string, int> { ["rogue"] = 30 })!;
        Assert.Equal(shared.CooldownRounds, highLevelShared.CooldownRounds);
        Assert.Equal(Assert.Single(SkillCatalog.EffectsFor(shared)).StatusCode,
            Assert.Single(SkillCatalog.EffectsFor(highLevelShared)).StatusCode);
    }

    [Fact]
    public async Task ShadowChargeCapsAtThreeCannotBeDispelledAndIsConsumedByExecutionSlash()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 20, monsterAttack: 1);
        test.Character.ProfessionCode = "rogue";
        test.Character.Level = 30;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "rogue-execution-slash", autoUse: true);
        var (service, monsterCombat) = CreateProductionSoulBattleService(test);
        for (var i = 0; i < 4; i++)
            await RogueMechanics.AddChargeAsync(monsterCombat.Statuses, test.Room, test.Character.Id, [], test.Character.Name);
        await test.Db.SaveChangesAsync();

        Assert.Equal(3, await monsterCombat.GetStatusStacksAsync(test.Room, "Character", test.Character.Id,
            "rogue-shadow-charge"));
        var displayedCharge = Assert.Single(await monsterCombat.GetStatusResponsesAsync(test.Room,
            "Character", test.Character.Id));
        Assert.True(displayedCharge.ExpiresWithRun);
        Assert.False(displayedCharge.CanDispel);
        Assert.Null(await monsterCombat.RemoveFirstStatusAsync(test.Room, "Character", [test.Character.Id], true));
        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("使用 斩击 攻击"));
        Assert.Equal(0, await monsterCombat.GetStatusStacksAsync(test.Room, "Character", test.Character.Id,
            "rogue-shadow-charge"));
    }

    [Fact]
    public async Task ThreeShadowChargesMultiplyExecutionSlashDamageBySixtyPercent()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1, monsterDefense: 0);
        test.Character.ProfessionCode = "rogue";
        test.Character.Level = 30;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "rogue-execution-slash", autoUse: true);
        var (service, monsterCombat) = CreateProductionSoulBattleService(test);
        for (var i = 0; i < 3; i++)
            await RogueMechanics.AddChargeAsync(monsterCombat.Statuses, test.Room, test.Character.Id, [], test.Character.Name);
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("斩击消耗 3 层") && log.Contains("提高 60%"));
        Assert.Contains(result.Logs, log => log.Contains("使用 斩击 攻击 Slime，造成 160 点伤害"));
    }

    [Fact]
    public async Task PoisonAndNativeAdrenalineCompleteSlashInSkillSlotOrder()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 20, monsterAttack: 1);
        test.Character.ProfessionCode = "rogue";
        test.Character.Level = 30;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "rogue-shadow-strike", autoUse: true);
        await test.AddSkillAsync(test.Character, 2, "rogue-poisoned-blade", autoUse: true);
        await test.AddSkillAsync(test.Character, 3, "rogue-adrenaline", autoUse: true);
        await test.AddSkillAsync(test.Character, 4, "rogue-execution-slash", autoUse: true);
        var (service, monsterCombat) = CreateProductionSoulBattleService(test);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("获得 影之蓄势（1 层）"));
        Assert.Contains(result.Logs, log => log.Contains("获得 影之蓄势（2 层）"));
        Assert.Contains(result.Logs, log => log.Contains("获得 影之蓄势（3 层）"));
        Assert.Contains(result.Logs, log => log.Contains("斩击消耗 3 层影之蓄势"));
        Assert.Equal(0, await monsterCombat.GetStatusStacksAsync(test.Room, "Character", test.Character.Id,
            "rogue-shadow-charge"));
    }

    [Theory]
    [InlineData(340, true)]
    [InlineData(1000, false)]
    [InlineData(1, false)]
    public async Task ExecutionSlashEchoRequiresLowHealthTargetThatSurvivesTheMainHit(int health, bool expectEcho)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 20, monsterAttack: 1);
        test.Character.ProfessionCode = "rogue";
        test.Character.Level = 30;
        test.Monster.MaxHp = 1000;
        test.Monster.Hp = health;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "rogue-execution-slash", autoUse: true);
        var (service, _) = CreateProductionSoulBattleService(test);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(expectEcho, result!.Logs.Any(log => log.Contains("斩杀追击")));
    }

    [Theory]
    [InlineData(10, 3)]
    [InlineData(20, 3)]
    [InlineData(30, 4)]
    public async Task BladeFlurryDealsTheRankSpecificNumberOfSeparateHits(int level, int hits)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 20, monsterAttack: 1);
        test.Character.ProfessionCode = "rogue";
        test.Character.Level = level;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "rogue-blade-flurry", autoUse: true);
        var (service, _) = CreateProductionSoulBattleService(test);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(hits, result!.Logs.Count(log => log.Contains("使用 刀锋乱舞 攻击")));
    }

    [Fact]
    public async Task ShadowChargeSurvivesTheNextWaveButClearsOnRunReset()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1);
        await test.EnableAutoForCharacterAsync(test.Character);
        test.Monster.RoomId = test.Room.Id;
        test.Monster.WaveNumber = 1;
        test.Monster.Position = 1;
        test.Monster.Hp = 1;
        test.Db.Monsters.Add(new Monster { RoomId = test.Room.Id, WaveNumber = 2, Position = 1,
            Name = "Second", Hp = 1, MaxHp = 1, Attack = 1, Defense = 0 });
        await test.Db.SaveChangesAsync();
        var (service, monsterCombat) = CreateProductionSoulBattleService(test);
        await RogueMechanics.AddChargeAsync(monsterCombat.Statuses, test.Room, test.Character.Id, [], test.Character.Name);
        await test.Db.SaveChangesAsync();

        var (first, firstError) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(firstError);
        Assert.Contains(first!.Logs, log => log.Contains("第 2 波"));
        Assert.Equal(1, await monsterCombat.GetStatusStacksAsync(test.Room, "Character", test.Character.Id,
            "rogue-shadow-charge"));

        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await test.Db.SaveChangesAsync();
        var (_, secondError) = await service.SyncAsync(test.Room.Id, test.Token);
        Assert.Null(secondError);
        Assert.Equal(RoomStatus.BattleOver, test.Room.Status);
        var (reset, resetError) = await service.ResetBattleAsync(test.Room.Id, test.Token);
        Assert.True(reset);
        Assert.Null(resetError);
        Assert.Equal(0, await monsterCombat.GetStatusStacksAsync(test.Room, "Character", test.Character.Id,
            "rogue-shadow-charge"));
    }

    [Fact]
    public async Task PoisonUsesTheCastAttackForExactlyThreeFutureTicks()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 40, monsterAttack: 1);
        test.Character.ProfessionCode = "rogue";
        test.Character.Level = 5;
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "rogue-poisoned-blade", autoUse: true);
        var (service, _) = CreateProductionSoulBattleService(test);

        var (cast, castError) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(castError);
        Assert.DoesNotContain(cast!.Logs, log => log.Contains("受到 淬毒 造成的"));
        var poison = await test.Db.BattleStatusEffects.SingleAsync(effect => effect.EffectCode == "rogue-poison");
        Assert.Equal(6, poison.PerTickValue);
        Assert.Equal(3, poison.ExpiresAfterRound - poison.AppliedRound);
        Assert.Equal(1, await test.Db.BattleStatusEffects.Where(effect => effect.EffectCode == "rogue-shadow-charge")
            .Select(effect => effect.Stacks).SingleAsync());
        test.Db.CharacterSkillSlots.Remove(await test.Db.CharacterSkillSlots.SingleAsync());
        test.Character.Attack = 100;
        await test.Db.SaveChangesAsync();

        for (var tick = 1; tick <= 3; tick++)
        {
            test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await test.Db.SaveChangesAsync();
            var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);
            Assert.Null(error);
            Assert.Single(result!.Logs, log => log.Contains("受到 淬毒 造成的 6 点伤害"));
        }
        Assert.Empty(await test.Db.BattleStatusEffects.Where(effect => effect.EffectCode == "rogue-poison").ToListAsync());
    }

    [Fact]
    public async Task RecastingPoisonRefreshesDurationWithoutWeakeningTheSnapshotOrSkippingADueTick()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 40, monsterAttack: 1);
        test.Character.ProfessionCode = "rogue";
        test.Character.Level = 5;
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "rogue-poisoned-blade", autoUse: true);
        var (service, _) = CreateProductionSoulBattleService(test);
        var (_, firstError) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(firstError);
        var poison = await test.Db.BattleStatusEffects.SingleAsync(effect => effect.EffectCode == "rogue-poison");
        Assert.Equal(6, poison.PerTickValue);

        test.Character.Attack = 20;
        var cooldown = await test.Db.BattleSkillCooldowns.SingleAsync(effect => effect.SkillCode == "rogue-poisoned-blade");
        cooldown.ReadyAtRound = test.Room.RoundNumber;
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        var (refresh, refreshError) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(refreshError);
        Assert.Single(refresh!.Logs, log => log.Contains("受到 淬毒 造成的 6 点伤害"));
        Assert.Equal(6, poison.PerTickValue);
        Assert.Equal(0, poison.AppliedRound);
        Assert.Equal(4, poison.ExpiresAfterRound);
        Assert.Single(await test.Db.BattleStatusEffects.Where(effect => effect.EffectCode == "rogue-poison").ToListAsync());
    }

    [Fact]
    public async Task AdrenalineBoostsNormalDoubleAttackDuringCastAndNextRoundOnly()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 10, monsterAttack: 1);
        test.Character.ProfessionCode = "rogue";
        test.Character.Level = 30;
        test.Character.WeaponDoubleAttackChancePercent = 70;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "rogue-adrenaline", autoUse: true);
        var (service, monsterCombat) = CreateProductionSoulBattleService(test);

        var (cast, castError) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(castError);
        Assert.Single(cast!.Logs, log => log.Contains("二连击"));
        Assert.Equal(30, await monsterCombat.GetModifierAsync(test.Room, "Character", test.Character.Id,
            "DoubleAttackChancePercent"));
        Assert.Equal(1, await monsterCombat.GetStatusStacksAsync(test.Room, "Character", test.Character.Id,
            "rogue-shadow-charge"));
        Assert.Single(await test.Db.BattleStatusEffects.Where(effect => effect.EffectCode.StartsWith("rogue-adrenaline-")).ToListAsync());

        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        var (next, nextError) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(nextError);
        Assert.Single(next!.Logs, log => log.Contains("二连击"));
        Assert.Equal(0, await monsterCombat.GetModifierAsync(test.Room, "Character", test.Character.Id,
            "DoubleAttackChancePercent"));
    }

    [Fact]
    public async Task SharedAdrenalineGrantsFixedThirtyPointsAndSixRoundCooldownToLowLevelRecipient()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 10, monsterAttack: 1);
        test.Character.ProfessionCode = "mage";
        test.Character.Level = 10;
        test.Character.WeaponDoubleAttackChancePercent = 70;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Db.CharacterCombatProfessions.Add(new CharacterCombatProfession
        {
            CharacterId = test.Character.Id, ProfessionCode = "rogue", Level = 30
        });
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "rogue-adrenaline", autoUse: true);
        var (service, monsterCombat) = CreateProductionSoulBattleService(test);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Single(result!.Logs, log => log.Contains("二连击"));
        Assert.Equal(30, await monsterCombat.GetModifierAsync(test.Room, "Character", test.Character.Id,
            "DoubleAttackChancePercent"));
        Assert.Equal("rogue-adrenaline-3", Assert.Single(await test.Db.BattleStatusEffects.ToListAsync()).EffectCode);
        Assert.Equal(0, await monsterCombat.GetStatusStacksAsync(test.Room, "Character", test.Character.Id,
            "rogue-shadow-charge"));
        Assert.Equal(7, (await test.Db.BattleSkillCooldowns.SingleAsync()).ReadyAtRound);
    }

    private static SkillCatalog LoadRogueCatalog()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();
        var monsters = new MonsterCombatCatalog(Options.Create(
            configuration.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!));
        return new SkillCatalog(Options.Create(configuration.GetSection(SkillOptions.SectionName).Get<SkillOptions>()!), monsters);
    }
}
