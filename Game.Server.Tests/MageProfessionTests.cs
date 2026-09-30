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
    public void MageCatalogHasFiveRankedSkillsAndFixedSharedSpellbreak()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();
        var catalog = new SkillCatalog(Options.Create(configuration.GetSection(SkillOptions.SectionName).Get<SkillOptions>()!));
        var mage = catalog.FindProfession("mage")!;
        Assert.Equal(new[] { "mage-arcane-bolt" }, mage.StartingSkills);
        Assert.Equal("mage-spellbreak", mage.SharedSkillCode);

        var expected = new[]
        {
            (Code: "mage-arcane-bolt", Unlock: 1, Initial: 0, Cooldown: 1),
            (Code: "mage-frost-bolt", Unlock: 3, Initial: 1, Cooldown: 3),
            (Code: "mage-scorch", Unlock: 5, Initial: 2, Cooldown: 4),
            (Code: "mage-spellbreak", Unlock: 7, Initial: 2, Cooldown: 4),
            (Code: "mage-arcane-domain", Unlock: 10, Initial: 3, Cooldown: 5)
        };
        foreach (var item in expected)
        {
            var skill = catalog.FindSkill(item.Code)!;
            Assert.Equal((item.Unlock, item.Initial, item.Cooldown),
                (skill.UnlockLevel, skill.InitialCooldownRounds, skill.CooldownRounds));
        }
        Assert.Equal(expected.Select(item => item.Code).Order(),
            catalog.SkillsForProfessionAtLevel("mage", 30).Select(skill => skill.Code).Order());

        var shared = catalog.ResolveSkillForLevel(new Character { ProfessionCode = "knight", Level = 30 },
            "mage-spellbreak", new Dictionary<string, int> { ["mage"] = 30 })!;
        Assert.Equal(6, shared.CooldownRounds);
        Assert.Equal("MonsterHasBuff", SkillCatalog.AutoConditionFor(shared));
        Assert.Equal(new[] { "Damage", "Dispel" }, SkillCatalog.EffectsFor(shared).Select(effect => effect.Type));
        Assert.Equal(30, SkillCatalog.EffectsFor(shared).First().AttackPowerPercent);
    }

    [Fact]
    public async Task ThreeDisorderStacksEchoOnceAndCarryTheRemainingStacks()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1,
            monsterDefense: 0);
        test.Character.ProfessionCode = "mage";
        test.Character.Level = 30;
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "mage-arcane-bolt", autoUse: true);
        await test.AddSkillAsync(test.Character, 2, "mage-frost-bolt", autoUse: true);
        await test.AddSkillAsync(test.Character, 3, "mage-scorch", autoUse: true);
        var (service, monsterCombat) = CreateProductionSoulBattleService(test);
        await MageMechanics.AddDisorderAsync(monsterCombat.Statuses, test.Room, test.Character.Id, test.Monster.Id, [], test.Character.Name);
        await MageMechanics.AddDisorderAsync(monsterCombat.Statuses, test.Room, test.Character.Id, test.Monster.Id, [], test.Character.Name);
        await test.Db.SaveChangesAsync();

        var (round, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Single(round!.Logs, log => log.Contains("触发失序回响") && log.Contains("30 点伤害"));
        Assert.Equal(2, await MageMechanics.DisorderStacksAsync(monsterCombat.Statuses, test.Room, test.Character.Id,
            test.Monster.Id));
        Assert.Contains(await test.Db.BattleStatusEffects.ToListAsync(), effect =>
            effect.TargetId == test.Monster.Id && effect.EffectCode == "mage-skill-disruption-15");
    }

    [Fact]
    public async Task RecastingScorchUsesTheNewAttackSnapshot()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var (_, monsterCombat) = CreateProductionSoulBattleService(test);
        await monsterCombat.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id,
            "mage-scorch-dot", 3, [], test.Monster.Name, 15);
        await monsterCombat.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id,
            "mage-scorch-dot", 3, [], test.Monster.Name, 10);
        await test.Db.SaveChangesAsync();

        var scorch = Assert.Single(await test.Db.BattleStatusEffects.Where(effect =>
            effect.EffectCode == "mage-scorch-dot").ToListAsync());
        Assert.Equal(10, scorch.PerTickValue);
    }

    [Fact]
    public async Task ArcaneDomainAddsDisorderForThreeRoundsAndThenExpires()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1,
            monsterDefense: 0);
        test.Character.ProfessionCode = "mage";
        test.Character.Level = 30;
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "mage-arcane-domain", autoUse: true);
        var (service, monsterCombat) = CreateProductionSoulBattleService(test);

        var (cast, castError) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(castError);
        Assert.Equal(2, await MageMechanics.DisorderStacksAsync(monsterCombat.Statuses, test.Room, test.Character.Id,
            test.Monster.Id));
        Assert.DoesNotContain(cast!.Logs, log => log.Contains("触发失序回响"));

        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        var (second, secondError) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(secondError);
        Assert.Single(second!.Logs, log => log.Contains("触发失序回响") && log.Contains("50 点伤害"));
        Assert.Equal(0, await MageMechanics.DisorderStacksAsync(monsterCombat.Statuses, test.Room, test.Character.Id,
            test.Monster.Id));
        Assert.Contains(await test.Db.BattleStatusEffects.ToListAsync(), effect =>
            effect.TargetId == test.Monster.Id && effect.EffectCode == "mage-skill-disruption-25");

        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        var (third, thirdError) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(thirdError);
        Assert.DoesNotContain(third!.Logs, log => log.Contains("触发失序回响"));
        Assert.Equal(1, await MageMechanics.DisorderStacksAsync(monsterCombat.Statuses, test.Room, test.Character.Id,
            test.Monster.Id));

        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        var (fourth, fourthError) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(fourthError);
        Assert.DoesNotContain(fourth!.Logs, log => log.Contains("触发失序回响"));
        Assert.Equal(1, await MageMechanics.DisorderStacksAsync(monsterCombat.Statuses, test.Room, test.Character.Id,
            test.Monster.Id));
        Assert.DoesNotContain(await test.Db.BattleStatusEffects.ToListAsync(), effect =>
            effect.EffectCode == "mage-domain-3");
    }

    [Fact]
    public async Task SpellbreakDispelsAgainNextRoundWithoutAnotherCastOrDisorder()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 20, monsterAttack: 1,
            monsterDefense: 0);
        test.Character.ProfessionCode = "mage";
        test.Character.Level = 7;
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "mage-spellbreak", autoUse: true);
        var (service, monsterCombat) = CreateProductionSoulBattleService(test);
        await monsterCombat.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id,
            "slime-shell", 5, [], test.Monster.Name);
        await monsterCombat.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id,
            "monster-attack-up", 5, [], test.Monster.Name);
        await test.Db.SaveChangesAsync();

        var (cast, castError) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(castError);
        Assert.Single(cast!.Logs, log => log.Contains("使用 法术反制，驱散了"));
        Assert.Equal(1, await MageMechanics.DisorderStacksAsync(monsterCombat.Statuses, test.Room, test.Character.Id,
            test.Monster.Id));

        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        var (second, secondError) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(secondError);
        Assert.Single(second!.Logs, log => log.Contains("法术反制持续驱散了"));
        Assert.DoesNotContain(second.Logs, log => log.Contains("使用 法术反制 攻击"));
        Assert.Equal(1, await MageMechanics.DisorderStacksAsync(monsterCombat.Statuses, test.Room, test.Character.Id,
            test.Monster.Id));
        Assert.DoesNotContain(await test.Db.BattleStatusEffects.ToListAsync(), effect =>
            effect.TargetType == "Monster" && effect.EffectCode is "slime-shell" or "monster-attack-up");
    }

    [Fact]
    public async Task SharedSpellbreakOnlyDispelsImmediatelyAndNeverAddsDisorder()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 20, monsterAttack: 1,
            monsterDefense: 0);
        test.Character.ProfessionCode = "knight";
        test.Character.Level = 10;
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        test.Db.CharacterCombatProfessions.Add(new CharacterCombatProfession
        {
            CharacterId = test.Character.Id, ProfessionCode = "mage", Level = 30
        });
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "mage-spellbreak", autoUse: true);
        var (service, monsterCombat) = CreateProductionSoulBattleService(test);
        await monsterCombat.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id,
            "slime-shell", 5, [], test.Monster.Name);
        await monsterCombat.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id,
            "monster-attack-up", 5, [], test.Monster.Name);
        await test.Db.SaveChangesAsync();

        var (cast, castError) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(castError);
        Assert.Single(cast!.Logs, log => log.Contains("使用 法术反制，驱散了"));
        Assert.Equal(0, await MageMechanics.DisorderStacksAsync(monsterCombat.Statuses, test.Room, test.Character.Id,
            test.Monster.Id));
        Assert.DoesNotContain(await test.Db.BattleStatusEffects.ToListAsync(), effect =>
            effect.EffectCode == "mage-spellbreak-continuous");
        Assert.Equal(7, Assert.Single(await test.Db.BattleSkillCooldowns.ToListAsync()).ReadyAtRound);

        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        var (second, secondError) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(secondError);
        Assert.DoesNotContain(second!.Logs, log => log.Contains("法术反制持续驱散了"));
        Assert.Single(await test.Db.BattleStatusEffects.Where(effect =>
            effect.TargetType == "Monster" &&
            (effect.EffectCode == "slime-shell" || effect.EffectCode == "monster-attack-up")).ToListAsync());
    }

    [Fact]
    public async Task MageDisruptionWaitsThroughBasicNonDamageAndInterruptedActionsThenReducesDamageSkill()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 100, characterAttack: 1,
            characterDefense: 0, monsterAttack: 10, monsterDefense: 0);
        var (_, monsterCombat) = CreateProductionSoulBattleService(test);
        var slot = await test.Db.RoomSlots.SingleAsync();
        var participants = new[] { new BattleParticipant(slot, test.Character) };
        await monsterCombat.Statuses.ApplyAsync(test.Room, "Monster", test.Monster.Id, "mage-skill-disruption-25", 0, [], test.Monster.Name);
        await test.Db.SaveChangesAsync();

        async Task<List<string>> ExecuteAsync(string actionType, string? skillCode = null, bool interrupted = false)
        {
            test.Db.MonsterIntents.Add(new MonsterIntent
            {
                RoomId = test.Room.Id, RunSequence = test.Room.RunSequence,
                RoundNumber = test.Room.RoundNumber, MonsterId = test.Monster.Id,
                ActionType = actionType, SkillCode = skillCode, IsInterrupted = interrupted,
                TargetType = skillCode == "slime-harden" ? "Self" : "Front",
                TargetCharacterId = skillCode == "slime-harden" ? null : test.Character.Id,
                CreatedAtUtc = DateTime.UtcNow
            });
            await test.Db.SaveChangesAsync();
            var logs = new List<string>();
            await monsterCombat.ExecuteIntentAsync(test.Room, test.Monster, participants,
                new Dictionary<int, Game.Shared.Enums.ElementType>(), logs);
            await test.Db.SaveChangesAsync();
            test.Room.RoundNumber++;
            return logs;
        }

        await ExecuteAsync("BasicAttack");
        Assert.True(await monsterCombat.HasStatusAsync(test.Room, "Monster", test.Monster.Id,
            "mage-skill-disruption-25"));
        await ExecuteAsync("Skill", "slime-harden");
        Assert.True(await monsterCombat.HasStatusAsync(test.Room, "Monster", test.Monster.Id,
            "mage-skill-disruption-25"));
        await ExecuteAsync("Skill", "slime-acid", interrupted: true);
        Assert.True(await monsterCombat.HasStatusAsync(test.Room, "Monster", test.Monster.Id,
            "mage-skill-disruption-25"));
        var hpBeforeSkill = test.Character.Hp;
        var damage = await ExecuteAsync("Skill", "slime-acid");
        Assert.Single(damage, log => log.Contains("奥术扰乱") && log.Contains("降低 25%"));
        Assert.Equal(9, hpBeforeSkill - test.Character.Hp); // 10 attack × 120% skill × 75% after disruption.
        Assert.False(await monsterCombat.HasStatusAsync(test.Room, "Monster", test.Monster.Id,
            "mage-skill-disruption-25"));
    }

    [Fact]
    public async Task TwoMagesAccumulateDisorderIndependentlyAgainstTheSameMonster()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 20, monsterAttack: 1,
            monsterDefense: 0);
        test.Character.ProfessionCode = "mage";
        test.Character.Level = 1;
        var secondMage = await test.AddSlotAsync(2, "Second Mage", attack: 20);
        secondMage.ProfessionCode = "mage";
        secondMage.Level = 1;
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "mage-arcane-bolt", autoUse: true);
        await test.AddSkillAsync(secondMage, 1, "mage-arcane-bolt", autoUse: true);
        var (service, monsterCombat) = CreateProductionSoulBattleService(test);
        await MageMechanics.AddDisorderAsync(monsterCombat.Statuses, test.Room, test.Character.Id, test.Monster.Id, [], test.Character.Name);
        await MageMechanics.AddDisorderAsync(monsterCombat.Statuses, test.Room, test.Character.Id, test.Monster.Id, [], test.Character.Name);
        await MageMechanics.AddDisorderAsync(monsterCombat.Statuses, test.Room, secondMage.Id, test.Monster.Id, [], secondMage.Name);
        await test.Db.SaveChangesAsync();
        var firstDisorder = Assert.Single(await monsterCombat.GetStatusResponsesAsync(test.Room, "Character", test.Character.Id),
            effect => effect.Code == "mage-disorder");
        var secondDisorder = Assert.Single(await monsterCombat.GetStatusResponsesAsync(test.Room, "Character", secondMage.Id),
            effect => effect.Code == "mage-disorder");
        Assert.Equal(2, firstDisorder.Stacks);
        Assert.Equal(1, secondDisorder.Stacks);
        Assert.Equal(test.Monster.Id, firstDisorder.BoundTargetId);
        Assert.Equal(test.Monster.Id, secondDisorder.BoundTargetId);
        Assert.Contains("目标死亡", firstDisorder.DurationText);
        Assert.DoesNotContain(await monsterCombat.GetStatusResponsesAsync(test.Room, "Monster", test.Monster.Id),
            effect => effect.Code == "mage-disorder");

        var (round, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Single(round!.Logs, log => log.Contains("触发失序回响") && log.Contains(test.Character.Name));
        Assert.Equal(0, await MageMechanics.DisorderStacksAsync(monsterCombat.Statuses, test.Room, test.Character.Id,
            test.Monster.Id));
        Assert.Equal(2, await MageMechanics.DisorderStacksAsync(monsterCombat.Statuses, test.Room, secondMage.Id,
            test.Monster.Id));
    }

    [Fact]
    public async Task DomainPersistsAcrossWaveButDisorderStartsOnTheNewMonster()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1,
            monsterDefense: 0);
        test.Character.ProfessionCode = "mage";
        test.Character.Level = 30;
        test.Monster.RoomId = test.Room.Id;
        test.Monster.WaveNumber = 1;
        test.Monster.Position = 1;
        test.Monster.Hp = 1;
        var secondMonster = new Monster
        {
            RoomId = test.Room.Id, WaveNumber = 2, Position = 1,
            Name = "Second", Hp = 10000, MaxHp = 10000, Attack = 1, Defense = 0
        };
        test.Db.Monsters.Add(secondMonster);
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "mage-arcane-domain", autoUse: true);
        var (service, monsterCombat) = CreateProductionSoulBattleService(test);

        var (first, firstError) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(firstError);
        Assert.Contains(first!.Logs, log => log.Contains("第 2 波"));
        Assert.True(await MageMechanics.DomainRankAsync(monsterCombat.Statuses, test.Room, test.Character.Id) > 0);
        Assert.Equal(0, await MageMechanics.DisorderStacksAsync(monsterCombat.Statuses, test.Room, test.Character.Id,
            secondMonster.Id));

        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await test.Db.SaveChangesAsync();
        var (second, secondError) = await service.SyncAsync(test.Room.Id, test.Token);

        Assert.Null(secondError);
        Assert.DoesNotContain(second!.Logs, log => log.Contains("触发失序回响"));
        Assert.Equal(1, await MageMechanics.DisorderStacksAsync(monsterCombat.Statuses, test.Room, test.Character.Id,
            secondMonster.Id));
        Assert.Equal(0, await MageMechanics.DisorderStacksAsync(monsterCombat.Statuses, test.Room, test.Character.Id,
            test.Monster.Id));
    }
}
