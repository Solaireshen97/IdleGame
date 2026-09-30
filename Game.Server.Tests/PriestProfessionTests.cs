using System.Text.RegularExpressions;
using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Fact]
    public void PriestCatalogHasFiveRankedSkillsAndFixedSharedCleanse()
    {
        var catalog = PriestCatalog();
        var profession = catalog.FindProfession("acolyte")!;
        Assert.Equal(new[] { "acolyte-holy-bolt" }, profession.StartingSkills);
        Assert.Equal("acolyte-purify", profession.SharedSkillCode);
        var expected = new[]
        {
            ("acolyte-holy-bolt", 1, 0, 1),
            ("acolyte-heal", 3, 1, 4),
            ("acolyte-purify", 5, 2, 4),
            ("acolyte-group-heal", 7, 2, 6),
            ("acolyte-revelation", 10, 3, 6)
        };
        Assert.Equal(expected.Select(item => item.Item1).Order(),
            catalog.SkillsForProfessionAtLevel("acolyte", 30).Select(skill => skill.Code).Order());
        foreach (var (code, unlock, initial, cooldown) in expected)
        {
            var skill = catalog.FindSkill(code)!;
            Assert.Equal((unlock, initial, cooldown),
                (skill.UnlockLevel, skill.InitialCooldownRounds, skill.CooldownRounds));
        }
        foreach (var (level, bolt, heal, group, ultimate) in new[]
                 { (10, 45, 12, 9, 80), (20, 50, 15, 12, 100), (30, 60, 18, 15, 120) })
        {
            var priest = new Character { ProfessionCode = "acolyte", Level = level };
            Assert.Equal(bolt, SkillCatalog.EffectsFor(catalog.ResolveSkillForLevel(priest,
                "acolyte-holy-bolt")!).Single().AttackPowerPercent);
            Assert.Equal(heal, SkillCatalog.EffectsFor(catalog.ResolveSkillForLevel(priest,
                "acolyte-heal")!).Single().HealMaxHpPercent);
            Assert.Equal(group, SkillCatalog.EffectsFor(catalog.ResolveSkillForLevel(priest,
                "acolyte-group-heal")!).Single().HealMaxHpPercent);
            Assert.Equal(ultimate, SkillCatalog.EffectsFor(catalog.ResolveSkillForLevel(priest,
                "acolyte-revelation")!).First(effect => effect.Type == "Damage").AttackPowerPercent);
        }
        var rankThreeCleanse = catalog.ResolveSkillForLevel(
            new Character { ProfessionCode = "acolyte", Level = 30 }, "acolyte-purify")!;
        Assert.Equal(3, rankThreeCleanse.CooldownRounds);
        var shared = catalog.ResolveSkillForLevel(new Character { ProfessionCode = "knight", Level = 30 },
            "acolyte-purify", new Dictionary<string, int> { ["acolyte"] = 30 })!;
        Assert.Equal((2, 5), (shared.InitialCooldownRounds, shared.CooldownRounds));
        Assert.Equal("AllyHasDebuff", SkillCatalog.AutoConditionFor(shared));
        Assert.Single(SkillCatalog.EffectsFor(shared), effect => effect.Type == "Cleanse");
    }

    [Fact]
    public async Task HolyBoltUsesSixtyPercentDamageAndOnlyOnePendingHealBoost()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1,
            monsterDefense: 0, characterDefense: 100);
        test.Character.ProfessionCode = "acolyte";
        test.Character.Level = 30;
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "acolyte-holy-bolt", autoUse: true);
        var (service, _) = CreateProductionSoulBattleService(test);

        for (var index = 0; index < 2; index++)
        {
            if (index > 0)
            {
                (await test.Db.BattleSkillCooldowns.SingleAsync()).ReadyAtRound = test.Room.RoundNumber;
                await PriestNextRoundAsync(test);
            }
            var (round, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);
            Assert.Null(error);
            Assert.Equal(60, PriestDamage(Assert.Single(round!.Logs, log =>
                log.Contains("使用 圣光弹 攻击"))));
        }
        Assert.Single(await test.Db.BattleStatusEffects.Where(effect =>
            effect.TargetId == test.Character.Id && effect.EffectCode == "acolyte-next-heal").ToListAsync());
    }

    [Fact]
    public async Task ManualHealTargetsChosenAllyAndConsumesOneTwentyPercentBoost()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1,
            monsterDefense: 0, characterDefense: 100);
        test.Character.ProfessionCode = "acolyte";
        test.Character.Level = 30;
        var chosen = await test.AddSlotAsync(2, "Chosen", hp: 50, attack: 1, defense: 100);
        var lower = await test.AddSlotAsync(3, "Lower", hp: 20, attack: 1, defense: 100);
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "acolyte-holy-bolt", autoUse: true);
        await test.AddSkillAsync(test.Character, 2, "acolyte-heal", autoUse: false);
        var (service, _) = CreateProductionSoulBattleService(test);
        Assert.Null((await service.StartPreparationAsync(test.Room.Id, test.Token)).Error);
        (await test.Db.CharacterSkillSlots.SingleAsync(slot => slot.SkillCode == "acolyte-holy-bolt"))
            .AutoUseEnabled = false;
        Assert.True((await service.QueueSkillAsync(new QueueSkillRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 2,
            IsQueued = true, TargetCharacterId = chosen.Id
        }, test.Token)).Success);
        await PriestNextRoundAsync(test);

        var (round, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(round!.Logs, log => log.Contains("使用 治愈祷言") && log.Contains("Chosen"));
        Assert.Equal(71, chosen.Hp); // floor(100 * 18% * 1.2) = 21.
        Assert.Equal(20, lower.Hp);
        Assert.DoesNotContain(await test.Db.BattleStatusEffects.ToListAsync(), effect =>
            effect.TargetId == test.Character.Id && effect.EffectCode == "acolyte-next-heal");
        Assert.Contains(await test.Db.BattleStatusEffects.ToListAsync(), effect =>
            effect.TargetId == test.Character.Id && effect.EffectCode == "acolyte-next-damage");
    }

    [Fact]
    public async Task AutomaticHealSelectsLowestInjuredAllyAndDoesNotCastAtFullHealth()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1,
            characterDefense: 100);
        test.Character.ProfessionCode = "acolyte";
        test.Character.Level = 30;
        var chosen = await test.AddSlotAsync(2, "Injured", hp: 50, attack: 1, defense: 100);
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "acolyte-heal", autoUse: true, threshold: 70);
        var (service, _) = CreateProductionSoulBattleService(test);
        var (first, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(error);
        Assert.Contains(first!.Logs, log => log.Contains("使用 治愈祷言") && log.Contains(chosen.Name));
        Assert.Equal(68, chosen.Hp);

        chosen.Hp = 100;
        (await test.Db.BattleSkillCooldowns.SingleAsync()).ReadyAtRound = test.Room.RoundNumber;
        await PriestNextRoundAsync(test);
        var (second, secondError) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(secondError);
        Assert.DoesNotContain(second!.Logs, log => log.Contains("使用 治愈祷言"));
    }

    [Fact]
    public async Task RankTwoCleanseRemovesOneDebuffFromChosenAllyAndPriest()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1,
            characterDefense: 100);
        test.Character.ProfessionCode = "acolyte";
        test.Character.Level = 20;
        var chosen = await test.AddSlotAsync(2, "Chosen", attack: 1, defense: 100);
        var other = await test.AddSlotAsync(3, "Other", attack: 1, defense: 100);
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "acolyte-purify", autoUse: false);
        var (service, combat) = CreateProductionSoulBattleService(test);
        foreach (var target in new[] { test.Character, chosen, other })
        {
            await combat.ApplyStatusAsync(test.Room, "Character", target.Id, "poison", 5, [], target.Name);
            await combat.ApplyStatusAsync(test.Room, "Character", target.Id, "armor-break", 5, [], target.Name);
        }
        await test.Db.SaveChangesAsync();
        Assert.True((await service.QueueSkillAsync(new QueueSkillRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 1,
            IsQueued = true, TargetCharacterId = chosen.Id
        }, test.Token)).Success);

        var (round, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(round!.Logs, log => log.Contains("净化祷言") && log.Contains(chosen.Name));
        var remaining = await test.Db.BattleStatusEffects.Where(effect =>
            effect.TargetType == "Character" &&
            (effect.EffectCode == "poison" || effect.EffectCode == "armor-break")).ToListAsync();
        Assert.Single(remaining, effect => effect.TargetId == chosen.Id);
        Assert.Single(remaining, effect => effect.TargetId == test.Character.Id);
        Assert.Equal(2, remaining.Count(effect => effect.TargetId == other.Id));
        Assert.Contains(await test.Db.BattleStatusEffects.ToListAsync(), effect =>
            effect.TargetId == test.Character.Id && effect.EffectCode == "acolyte-next-damage");
    }

    [Fact]
    public async Task RankTwoCleanseTargetingSelfRemovesOnlyOneDebuff()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1,
            characterDefense: 100);
        test.Character.ProfessionCode = "acolyte";
        test.Character.Level = 20;
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "acolyte-purify", autoUse: false);
        var (service, combat) = CreateProductionSoulBattleService(test);
        await combat.ApplyStatusAsync(test.Room, "Character", test.Character.Id, "poison", 5, [], test.Character.Name);
        await combat.ApplyStatusAsync(test.Room, "Character", test.Character.Id, "armor-break", 5, [], test.Character.Name);
        await test.Db.SaveChangesAsync();
        Assert.True((await service.QueueSkillAsync(new QueueSkillRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 1,
            IsQueued = true, TargetCharacterId = test.Character.Id
        }, test.Token)).Success);

        var (_, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Single(await test.Db.BattleStatusEffects.Where(effect => effect.TargetId == test.Character.Id &&
            (effect.EffectCode == "poison" || effect.EffectCode == "armor-break")).ToListAsync());
    }

    [Fact]
    public async Task AutomaticCleanseSelectsDebuffedAlly()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1,
            characterDefense: 100);
        test.Character.ProfessionCode = "acolyte";
        test.Character.Level = 10;
        var chosen = await test.AddSlotAsync(2, "Debuffed", attack: 1, defense: 100);
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "acolyte-purify", autoUse: true);
        var (service, combat) = CreateProductionSoulBattleService(test);
        await combat.ApplyStatusAsync(test.Room, "Character", chosen.Id, "poison", 5, [], chosen.Name);
        await test.Db.SaveChangesAsync();

        var (round, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(round!.Logs, log => log.Contains("净化祷言") && log.Contains(chosen.Name));
        Assert.DoesNotContain(await test.Db.BattleStatusEffects.ToListAsync(), effect =>
            effect.TargetId == chosen.Id && effect.EffectCode == "poison");
    }

    [Fact]
    public async Task SharedCleanseRemovesOnlyTargetDebuffAndDoesNotPrepareDamage()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1,
            characterDefense: 100);
        test.Character.ProfessionCode = "knight";
        test.Character.Level = 30;
        var chosen = await test.AddSlotAsync(2, "Chosen", attack: 1, defense: 100);
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        test.Db.CharacterCombatProfessions.Add(new CharacterCombatProfession
        {
            CharacterId = test.Character.Id, ProfessionCode = "acolyte", Level = 30
        });
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "acolyte-purify", autoUse: false);
        var (service, combat) = CreateProductionSoulBattleService(test);
        // The shared version retains its two-round initial cooldown.
        for (var roundIndex = 0; roundIndex < 2; roundIndex++)
        {
            if (roundIndex > 0) await PriestNextRoundAsync(test);
            Assert.Null((await service.StartPreparationAsync(test.Room.Id, test.Token)).Error);
        }
        foreach (var target in new[] { test.Character, chosen })
        {
            await combat.ApplyStatusAsync(test.Room, "Character", target.Id, "poison", 5, [], target.Name);
            await combat.ApplyStatusAsync(test.Room, "Character", target.Id, "armor-break", 5, [], target.Name);
        }
        await test.Db.SaveChangesAsync();
        Assert.True((await service.QueueSkillAsync(new QueueSkillRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 1,
            IsQueued = true, TargetCharacterId = chosen.Id
        }, test.Token)).Success);
        await PriestNextRoundAsync(test);
        var (_, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        var remaining = await test.Db.BattleStatusEffects.ToListAsync();
        Assert.Single(remaining, effect => effect.TargetId == chosen.Id &&
            effect.EffectCode is "poison" or "armor-break");
        Assert.Equal(2, remaining.Count(effect => effect.TargetId == test.Character.Id &&
            effect.EffectCode is "poison" or "armor-break"));
        Assert.DoesNotContain(remaining, effect => effect.EffectCode == "acolyte-next-damage");
        Assert.Equal(8, Assert.Single(await test.Db.BattleSkillCooldowns.ToListAsync()).ReadyAtRound);
    }

    [Fact]
    public async Task RevelationEmpowersThreeFollowingDamageSkillsWithoutStackingNormalBoost()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1,
            monsterDefense: 0, characterDefense: 100);
        test.Character.ProfessionCode = "acolyte";
        test.Character.Level = 30;
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "acolyte-revelation", autoUse: true);
        await test.AddSkillAsync(test.Character, 2, "acolyte-holy-bolt", autoUse: true);
        var (service, combat) = CreateProductionSoulBattleService(test);

        var hits = new List<int>();
        for (var index = 0; index < 4; index++)
        {
            if (index > 0)
            {
                if (index == 1)
                    await combat.ApplyStatusAsync(test.Room, "Character", test.Character.Id,
                        "acolyte-next-damage", 20, [], test.Character.Name);
                (await test.Db.BattleSkillCooldowns.SingleAsync(cooldown =>
                    cooldown.SkillCode == "acolyte-holy-bolt")).ReadyAtRound = test.Room.RoundNumber;
                await PriestNextRoundAsync(test);
            }
            var (round, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);
            Assert.Null(error);
            if (index == 0)
                Assert.Equal(120, PriestDamage(Assert.Single(round!.Logs, log =>
                    log.Contains("使用 神启裁决 攻击"))));
            hits.Add(PriestDamage(Assert.Single(round!.Logs, log =>
                log.Contains("使用 圣光弹 攻击"))));
        }
        Assert.Equal(new[] { 69, 69, 69, 60 }, hits);
    }

    [Fact]
    public async Task GroupPrayerHealsEveryLivingAllyAndCannotSelectOneTarget()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 50, characterAttack: 1,
            monsterAttack: 1, characterDefense: 100);
        test.Character.ProfessionCode = "acolyte";
        test.Character.Level = 30;
        var ally = await test.AddSlotAsync(2, "Ally", hp: 60, attack: 1, defense: 100);
        var full = await test.AddSlotAsync(3, "Full", hp: 100, attack: 1, defense: 100);
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "acolyte-group-heal", autoUse: false);
        var (service, _) = CreateProductionSoulBattleService(test);
        var selected = await service.QueueSkillAsync(new QueueSkillRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 1,
            IsQueued = true, TargetCharacterId = ally.Id
        }, test.Token);
        Assert.False(selected.Success);
        Assert.True((await service.QueueSkillAsync(new QueueSkillRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 1, IsQueued = true
        }, test.Token)).Success);

        var (round, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(round!.Logs, log => log.Contains("群体祷言"));
        Assert.Contains(round.Logs, log => log.Contains("群体祷言") &&
            log.Contains(test.Character.Name) && log.Contains("恢复 15 点"));
        Assert.Equal(64, test.Character.Hp); // Monster's one-point counterattack follows the heal.
        Assert.Equal(75, ally.Hp);
        Assert.Equal(100, full.Hp);
        Assert.Single(await test.Db.BattleStatusEffects.Where(effect =>
            effect.TargetId == test.Character.Id && effect.EffectCode == "acolyte-next-damage").ToListAsync());
    }

    [Fact]
    public async Task PendingHealBoostCarriesToNextWave()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1,
            monsterDefense: 0, characterDefense: 100);
        test.Character.ProfessionCode = "acolyte";
        test.Character.Level = 30;
        test.Monster.RoomId = test.Room.Id;
        test.Monster.WaveNumber = 1;
        test.Monster.Position = 1;
        test.Monster.Hp = 1;
        var next = new Monster
        {
            RoomId = test.Room.Id, WaveNumber = 2, Position = 1, Name = "Next",
            Hp = 10000, MaxHp = 10000, Attack = 1, Defense = 0
        };
        test.Db.Monsters.Add(next);
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "acolyte-holy-bolt", autoUse: true);
        var (service, _) = CreateProductionSoulBattleService(test);

        var (first, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(first!.Logs, log => log.Contains("第 2 波"));
        Assert.Contains(await test.Db.BattleStatusEffects.ToListAsync(), effect =>
            effect.TargetId == test.Character.Id && effect.EffectCode == "acolyte-next-heal");
        var boltSlot = await test.Db.CharacterSkillSlots.SingleAsync();
        boltSlot.AutoUseEnabled = false;
        test.Character.Hp = 50;
        await test.AddSkillAsync(test.Character, 2, "acolyte-heal", autoUse: true, threshold: 70);
        await PriestNextRoundAsync(test);

        Assert.Null((await service.SyncAsync(test.Room.Id, test.Token)).Error);
        await PriestNextRoundAsync(test);
        var (second, secondError) = await service.SyncAsync(test.Room.Id, test.Token);

        Assert.Null(secondError);
        Assert.Contains(second!.Logs, log => log.Contains("治愈祷言") &&
            log.Contains("恢复 21 点"));
        Assert.Equal(70, test.Character.Hp);
    }

    [Fact]
    public async Task TwoPriestsKeepAndConsumeTheirOwnBoostsIndependently()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1,
            monsterDefense: 0, characterDefense: 100);
        test.Character.ProfessionCode = "acolyte";
        test.Character.Level = 30;
        var second = await test.AddSlotAsync(2, "Second Priest", hp: 50, attack: 100, defense: 100);
        second.ProfessionCode = "acolyte";
        second.Level = 30;
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "acolyte-holy-bolt", autoUse: true);
        await test.AddSkillAsync(second, 1, "acolyte-holy-bolt", autoUse: true);
        var (service, _) = CreateProductionSoulBattleService(test);
        Assert.Null((await service.StartPreparationAsync(test.Room.Id, test.Token)).Error);
        var buffs = await test.Db.BattleStatusEffects.Where(effect =>
            effect.EffectCode == "acolyte-next-heal").ToListAsync();
        Assert.Equal(new[] { test.Character.Id, second.Id }.Order(),
            buffs.Select(effect => effect.TargetId).Order());

        foreach (var slot in await test.Db.CharacterSkillSlots.ToListAsync()) slot.AutoUseEnabled = false;
        await test.AddSkillAsync(test.Character, 2, "acolyte-heal", autoUse: false);
        Assert.True((await service.QueueSkillAsync(new QueueSkillRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 2,
            IsQueued = true, TargetCharacterId = second.Id
        }, test.Token)).Success);
        await PriestNextRoundAsync(test);
        var (_, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(71, second.Hp);
        buffs = await test.Db.BattleStatusEffects.Where(effect =>
            effect.EffectCode == "acolyte-next-heal").ToListAsync();
        Assert.Equal(second.Id, Assert.Single(buffs).TargetId);
        Assert.Contains(await test.Db.BattleStatusEffects.ToListAsync(), effect =>
            effect.TargetId == test.Character.Id && effect.EffectCode == "acolyte-next-damage");
    }

    private static SkillCatalog PriestCatalog()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        return new SkillCatalog(Options.Create(configuration.GetSection(SkillOptions.SectionName).Get<SkillOptions>()!));
    }

    private static async Task PriestNextRoundAsync(BattleTestContext test)
    {
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
    }

    private static int PriestDamage(string log)
    {
        var match = Regex.Match(log, @"造成 (\d+) 点伤害");
        Assert.True(match.Success, $"Damage was not found in battle log: {log}");
        return int.Parse(match.Groups[1].Value);
    }
}
