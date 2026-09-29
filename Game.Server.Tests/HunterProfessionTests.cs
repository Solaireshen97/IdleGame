using System.Text.RegularExpressions;
using Game.Server.Services;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Fact]
    public async Task TrackingMarksBelongToEachHunterAndPrecisionOnlyConsumesItsOwnMark()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1,
            monsterDefense: 0);
        test.Character.ProfessionCode = "hunter";
        test.Character.Level = 30;
        var second = await test.AddSlotAsync(2, "Second Hunter", attack: 100);
        second.ProfessionCode = "hunter";
        second.Level = 30;
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "hunter-tracking-shot", autoUse: true);
        await test.AddSkillAsync(second, 1, "hunter-tracking-shot", autoUse: true);
        var (service, combat) = CreateProductionSoulBattleService(test);

        var (first, firstError) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(firstError);
        Assert.Equal(2, first!.Logs.Count(log => log.Contains("使用 追踪射击 攻击")));
        Assert.True(await combat.HasHunterMarkAsync(test.Room, test.Character.Id, test.Monster.Id));
        Assert.True(await combat.HasHunterMarkAsync(test.Room, second.Id, test.Monster.Id));
        var marks = await test.Db.BattleStatusEffects.Where(effect => effect.EffectCode == "hunter-prey-mark").ToListAsync();
        Assert.Equal(2, marks.Count);
        Assert.All(marks, mark => Assert.Equal(test.Monster.Id, mark.PerTickValue));
        Assert.Equal(new[] { test.Character.Id, second.Id }.Order(), marks.Select(mark => mark.TargetId).Order());

        await test.AddSkillAsync(test.Character, 2, "hunter-precision-shot", autoUse: true);
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        var (secondRound, secondError) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(secondError);
        Assert.Contains(secondRound!.Logs, log => log.Contains("使用 精准射击 攻击"));
        Assert.False(await combat.HasHunterMarkAsync(test.Room, test.Character.Id, test.Monster.Id));
        Assert.True(await combat.HasHunterMarkAsync(test.Room, second.Id, test.Monster.Id));
    }

    [Fact]
    public async Task TrackingMarkExpiresAfterFourCombatRounds()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1,
            monsterDefense: 0);
        test.Character.ProfessionCode = "hunter";
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.EnableAutoForCharacterAsync(test.Character);
        var (service, combat) = CreateProductionSoulBattleService(test);
        await combat.ApplyHunterMarkAsync(test.Room, test.Character.Id, test.Monster.Id, 3, [],
            test.Character.Name);
        await test.Db.SaveChangesAsync();

        for (var roundNumber = 1; roundNumber <= 4; roundNumber++)
        {
            if (roundNumber > 1)
            {
                test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
                await test.Db.SaveChangesAsync();
            }
            var (_, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);
            Assert.Null(error);
            Assert.Equal(roundNumber < 4,
                await combat.HasHunterMarkAsync(test.Room, test.Character.Id, test.Monster.Id));
        }
    }

    [Fact]
    public async Task PrecisionShotUsesMarkedDamageInOneHitAndConsumesTheMark()
    {
        async Task<(int Damage, bool MarkRemains, int HitLogs)> CastAsync(bool marked)
        {
            await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1,
                monsterDefense: 0);
            test.Character.ProfessionCode = "hunter";
            test.Character.Level = 30;
            test.Monster.Hp = test.Monster.MaxHp = 10000;
            await test.Db.SaveChangesAsync();
            await test.AddSkillAsync(test.Character, 1, "hunter-precision-shot", autoUse: true);
            var (service, combat) = CreateProductionSoulBattleService(test);
            if (marked)
            {
                await combat.ApplyHunterMarkAsync(test.Room, test.Character.Id, test.Monster.Id, 4, [],
                    test.Character.Name);
                await test.Db.SaveChangesAsync();
            }

            var (round, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

            Assert.Null(error);
            var hits = round!.Logs.Where(log => log.Contains("使用 精准射击 攻击")).ToList();
            return (HunterDamage(hits.Single()),
                await combat.HasHunterMarkAsync(test.Room, test.Character.Id, test.Monster.Id), hits.Count);
        }

        var plain = await CastAsync(false);
        var marked = await CastAsync(true);

        Assert.Equal(1, plain.HitLogs);
        Assert.Equal(1, marked.HitLogs);
        Assert.True(marked.Damage > plain.Damage);
        Assert.False(marked.MarkRemains);
    }

    [Fact]
    public async Task ExposeShotConsumesMarkAndItsVulnerabilityRaisesNormalDamage()
    {
        async Task<(int NormalDamage, string[] Vulnerabilities, bool MarkRemains)> CastAsync(bool marked)
        {
            await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1,
                monsterDefense: 0);
            test.Character.ProfessionCode = "hunter";
            test.Character.Level = 30;
            test.Monster.Hp = test.Monster.MaxHp = 10000;
            await test.Db.SaveChangesAsync();
            await test.AddSkillAsync(test.Character, 1, "hunter-expose-shot", autoUse: true);
            var (service, combat) = CreateProductionSoulBattleService(test);
            if (marked)
            {
                await combat.ApplyHunterMarkAsync(test.Room, test.Character.Id, test.Monster.Id, 4, [],
                    test.Character.Name);
                await test.Db.SaveChangesAsync();
            }

            var (round, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

            Assert.Null(error);
            var normal = round!.Logs.Single(log => log.Contains($"{test.Character.Name} 普通攻击 "));
            var statuses = await test.Db.BattleStatusEffects.Where(effect =>
                effect.EffectCode.StartsWith("hunter-vulnerability-")).Select(effect => effect.EffectCode).ToArrayAsync();
            return (HunterDamage(normal), statuses,
                await combat.HasHunterMarkAsync(test.Room, test.Character.Id, test.Monster.Id));
        }

        var plain = await CastAsync(false);
        var marked = await CastAsync(true);

        Assert.Empty(plain.Vulnerabilities);
        Assert.Equal(new[] { "hunter-vulnerability-12" }, marked.Vulnerabilities);
        Assert.True(marked.NormalDamage > plain.NormalDamage);
        Assert.False(marked.MarkRemains);
    }

    [Fact]
    public async Task SharedExposeShotAppliesFixedVulnerabilityWithoutAHunterMark()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1,
            monsterDefense: 0);
        test.Character.ProfessionCode = "knight";
        test.Character.Level = 30;
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        test.Db.CharacterCombatProfessions.Add(new CharacterCombatProfession
        {
            CharacterId = test.Character.Id, ProfessionCode = "hunter", Level = 30
        });
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "hunter-expose-shot", autoUse: true);
        var (service, combat) = CreateProductionSoulBattleService(test);

        var (round, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(30, HunterDamage(Assert.Single(round!.Logs, log =>
            log.Contains("使用 破绽射击 攻击"))));
        Assert.Contains(await test.Db.BattleStatusEffects.ToListAsync(), effect =>
            effect.EffectCode == "hunter-vulnerability-10-shared");
        Assert.False(await combat.HasHunterMarkAsync(test.Room, test.Character.Id, test.Monster.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TwoExposeShotsKeepOnlyTheStrongerVulnerability(bool strongerFirst)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1,
            monsterDefense: 0);
        test.Character.ProfessionCode = "hunter";
        test.Character.Level = strongerFirst ? 30 : 1;
        var second = await test.AddSlotAsync(2, "Second Hunter", attack: 100);
        second.ProfessionCode = "hunter";
        second.Level = strongerFirst ? 1 : 30;
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "hunter-expose-shot", autoUse: true);
        await test.AddSkillAsync(second, 1, "hunter-expose-shot", autoUse: true);
        var (service, combat) = CreateProductionSoulBattleService(test);
        await combat.ApplyHunterMarkAsync(test.Room, test.Character.Id, test.Monster.Id, 4, [],
            test.Character.Name);
        await combat.ApplyHunterMarkAsync(test.Room, second.Id, test.Monster.Id, 4, [], second.Name);
        await test.Db.SaveChangesAsync();

        var (round, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(2, round!.Logs.Count(log => log.Contains("使用 破绽射击 攻击")));
        var vulnerabilities = await test.Db.BattleStatusEffects.Where(effect =>
            effect.EffectCode.StartsWith("hunter-vulnerability-")).ToListAsync();
        Assert.Equal("hunter-vulnerability-12", Assert.Single(vulnerabilities).EffectCode);
    }

    [Fact]
    public async Task ADefeatedMonsterLeavesNoHunterMarkOnTheNextWave()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1,
            monsterDefense: 0);
        test.Character.ProfessionCode = "hunter";
        test.Character.Level = 30;
        test.Monster.RoomId = test.Room.Id;
        test.Monster.WaveNumber = 1;
        test.Monster.Position = 1;
        test.Monster.Hp = 1;
        var next = new Monster
        {
            RoomId = test.Room.Id, WaveNumber = 2, Position = 1,
            Name = "Second", Hp = 10000, MaxHp = 10000, Attack = 1, Defense = 0
        };
        test.Db.Monsters.Add(next);
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "hunter-tracking-shot", autoUse: true);
        var (service, combat) = CreateProductionSoulBattleService(test);

        var (round, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(round!.Logs, log => log.Contains("第 2 波"));
        Assert.False(await combat.HasHunterMarkAsync(test.Room, test.Character.Id, test.Monster.Id));
        Assert.False(await combat.HasHunterMarkAsync(test.Room, test.Character.Id, next.Id));
        Assert.Empty(await test.Db.BattleStatusEffects.Where(effect =>
            effect.EffectCode == "hunter-prey-mark").ToListAsync());
    }

    [Fact]
    public async Task HuntingSignalGivesHunterAndAllyOneStrongerPursuitWhenMarked()
    {
        async Task<int[]> CastAsync(bool marked)
        {
            await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1,
                monsterDefense: 0);
            test.Character.ProfessionCode = "hunter";
            test.Character.Level = 30;
            var ally = await test.AddSlotAsync(2, "Ally", attack: 100);
            test.Monster.Hp = test.Monster.MaxHp = 10000;
            await test.Db.SaveChangesAsync();
            await test.AddSkillAsync(test.Character, 1, "hunter-hunting-signal", autoUse: true);
            var (service, combat) = CreateProductionSoulBattleService(test);
            if (marked)
            {
                await combat.ApplyHunterMarkAsync(test.Room, test.Character.Id, test.Monster.Id, 4, [],
                    test.Character.Name);
                await test.Db.SaveChangesAsync();
            }

            var (round, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

            Assert.Null(error);
            Assert.Contains(round!.Logs, log => log.Contains(ally.Name) && log.Contains("普通攻击"));
            return round.Logs.Where(log => log.Contains("普攻追击伤害"))
                .Select(HunterDamage).Order().ToArray();
        }

        var plain = await CastAsync(false);
        var marked = await CastAsync(true);

        Assert.Equal(2, plain.Length);
        Assert.Equal(2, marked.Length);
        Assert.True(marked[0] > plain[0]);
        Assert.True(marked[1] > plain[1]);
    }

    [Fact]
    public async Task HuntingSignalAddsItsPursuitBeyondTheWeaponCap()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1,
            monsterDefense: 0);
        test.Character.ProfessionCode = "hunter";
        test.Character.Level = 30;
        test.Character.WeaponNormalEchoPercent = 100;
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "hunter-hunting-signal", autoUse: true);
        var (service, combat) = CreateProductionSoulBattleService(test);
        await combat.ApplyHunterMarkAsync(test.Room, test.Character.Id, test.Monster.Id, 4, [],
            test.Character.Name);
        await test.Db.SaveChangesAsync();

        var (round, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        var normal = HunterDamage(Assert.Single(round!.Logs, log =>
            log.Contains($"{test.Character.Name} 普通攻击 ")));
        var pursuit = HunterDamage(Assert.Single(round.Logs, log => log.Contains("普攻追击伤害")));
        Assert.Equal((int)decimal.Floor(normal * 1.2m), pursuit);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TwoHuntingSignalsKeepTheStrongerTeamBonus(bool strongerFirst)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1,
            monsterDefense: 0);
        test.Character.ProfessionCode = "hunter";
        test.Character.Level = strongerFirst ? 30 : 1;
        var second = await test.AddSlotAsync(2, "Second Hunter", attack: 100);
        second.ProfessionCode = "hunter";
        second.Level = strongerFirst ? 1 : 30;
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "hunter-hunting-signal", autoUse: true);
        await test.AddSkillAsync(second, 1, "hunter-hunting-signal", autoUse: true);
        var (service, combat) = CreateProductionSoulBattleService(test);
        var stronger = strongerFirst ? test.Character : second;
        await combat.ApplyHunterMarkAsync(test.Room, stronger.Id, test.Monster.Id, 4, [], stronger.Name);
        await test.Db.SaveChangesAsync();

        var (round, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(2, round!.Logs.Count(log => log.Contains("使用 协猎信号 攻击")));
        var coordinated = await test.Db.BattleStatusEffects.Where(effect =>
            effect.EffectCode.StartsWith("hunter-coordinated-")).ToListAsync();
        Assert.Equal(2, coordinated.Count);
        Assert.All(coordinated, effect => Assert.Equal("hunter-coordinated-20", effect.EffectCode));
        Assert.Equal(new[] { test.Character.Id, second.Id }.Order(),
            coordinated.Select(effect => effect.TargetId).Order());
    }

    [Fact]
    public async Task EagleEyeProtectsTwoLaterMarkConsumptionsThenTheNextConsumesIt()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 100, monsterAttack: 1,
            monsterDefense: 0);
        test.Character.ProfessionCode = "hunter";
        test.Character.Level = 30;
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "hunter-eagle-eye", autoUse: true);
        await test.AddSkillAsync(test.Character, 2, "hunter-precision-shot", autoUse: true);
        var (service, combat) = CreateProductionSoulBattleService(test);

        for (var cast = 1; cast <= 3; cast++)
        {
            if (cast > 1)
            {
                test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
                var precisionCooldown = await test.Db.BattleSkillCooldowns.SingleAsync(cooldown =>
                    cooldown.SkillCode == "hunter-precision-shot");
                precisionCooldown.ReadyAtRound = test.Room.RoundNumber;
                await test.Db.SaveChangesAsync();
            }

            var (round, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

            Assert.Null(error);
            Assert.Contains(round!.Logs, log => log.Contains("使用 精准射击 攻击"));
            Assert.Equal(cast < 3, await combat.HasHunterMarkAsync(test.Room, test.Character.Id,
                test.Monster.Id));
        }
    }

    private static int HunterDamage(string log)
    {
        var match = Regex.Match(log, @"造成 (\d+) 点(?:普攻追击)?伤害");
        Assert.True(match.Success, $"Damage was not found in battle log: {log}");
        return int.Parse(match.Groups[1].Value);
    }
}
