using Game.Server.Services;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Theory]
    [Trait("Category", "TalentBalance")]
    [InlineData("mage", "mage-frost-ward", "mage-frozen-heart", true, true, "arcane-weakness", true)]
    [InlineData("rogue", "rogue-evasion", "rogue-escape-artist", true, true, "arcane-weakness", true)]
    [InlineData("mage", "mage-frost-ward", "mage-frozen-heart", true, false, "arcane-weakness", false)]
    [InlineData("rogue", "rogue-evasion", "rogue-escape-artist", true, false, "arcane-weakness", false)]
    [InlineData("mage", "mage-frost-ward", "mage-frozen-heart", true, true, "warrior-fury-risk", false)]
    [InlineData("rogue", "rogue-evasion", "rogue-escape-artist", true, true, "warrior-fury-risk", false)]
    [InlineData("mage", "mage-frost-ward", "mage-frozen-heart", false, true, "arcane-weakness", false)]
    [InlineData("rogue", "rogue-evasion", "rogue-escape-artist", false, true, "arcane-weakness", false)]
    public async Task DefensiveCapstoneAutoOnlyCleansesItsOwnersRemovableDebuffAtFullHealth(
        string profession, string skillCode, string talentCode, bool hasTalent, bool onSelf,
        string statusCode, bool shouldCast)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        test.Character.ProfessionCode = profession;
        test.Character.Level = 10;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        var ally = await test.AddSlotAsync(2, "Ally", attack: 1);
        if (hasTalent)
            test.Db.CharacterSkillTalents.Add(new() { CharacterId = test.Character.Id, NodeCode = talentCode, PointsSpent = 1 });
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, skillCode, autoUse: true, threshold: 70);
        await EnableTalentTestAutoAsync(test);
        var (service, monsterCombat) = CreateProfessionBalanceService(test);
        var targetId = onSelf ? test.Character.Id : ally.Id;
        await monsterCombat.ApplyStatusAsync(test.Room, "Character", targetId, statusCode, 2, [], "目标");
        await test.Db.SaveChangesAsync();

        var (_, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(shouldCast, await test.Db.BattleSkillCooldowns.AnyAsync(item => item.SkillCode == skillCode));
        Assert.Equal(!shouldCast, await test.Db.BattleStatusEffects.AnyAsync(item =>
            item.TargetId == targetId && item.TargetType == "Character" && item.EffectCode == statusCode));
    }

    [Fact]
    [Trait("Category", "TalentBalance")]
    public async Task AfterglowProtectsFullHealthAlliesWhenGroupHealingAnInjuredAlly()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 20);
        var injured = await test.AddSlotAsync(2, "Injured", hp: 60, attack: 1);
        await PrepareTalentPriestAsync(test, afterglow: true);
        await test.AddSkillAsync(test.Character, 1, "priest-group-heal", autoUse: true, threshold: 70);
        var (service, _) = CreateProfessionBalanceService(test);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(80, injured.Hp);
        Assert.Equal(83, test.Character.Hp);
        Assert.Contains(await test.Db.BattleStatusEffects.ToListAsync(), effect =>
            effect.TargetId == test.Character.Id && effect.EffectCode == "mercy-ward");
        Assert.DoesNotContain(result!.Logs, log => log.Contains("恢复 0 点"));
    }

    [Theory]
    [Trait("Category", "TalentBalance")]
    [InlineData("priest-group-heal")]
    [InlineData("acolyte-heal")]
    public async Task AfterglowDoesNotAutoCastHealingWithoutAnInjuredOrDebuffedTarget(string skillCode)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        await test.AddSlotAsync(2, "Healthy", attack: 1);
        await PrepareTalentPriestAsync(test, afterglow: true);
        await test.AddSkillAsync(test.Character, 1, skillCode, autoUse: true, threshold: 100);
        var (service, _) = CreateProfessionBalanceService(test);

        var (_, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Empty(await test.Db.BattleSkillCooldowns.ToListAsync());
        Assert.DoesNotContain(await test.Db.BattleStatusEffects.ToListAsync(), effect => effect.EffectCode == "mercy-ward");
    }

    [Theory]
    [Trait("Category", "TalentBalance")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupHealUsedOnlyForCleansingDoesNotConsumeOrCreateHolyEcho(bool afterglow)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        await PrepareTalentPriestAsync(test, afterglow);
        test.Db.CharacterSkillTalents.Add(new() { CharacterId = test.Character.Id, NodeCode = "acolyte-echo", PointsSpent = 1 });
        test.Db.BattleStatusEffects.Add(new()
        {
            RoomId = test.Room.Id, RunSequence = test.Room.RunSequence, TargetType = "Character",
            TargetId = test.Character.Id, EffectCode = "talent-holy-heal", Stacks = 1,
            AppliedRound = test.Room.RoundNumber, ExpiresAfterRound = test.Room.RoundNumber + 3
        });
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "priest-group-heal", autoUse: true, threshold: 100);
        var (service, monsterCombat) = CreateProfessionBalanceService(test);
        await monsterCombat.ApplyStatusAsync(test.Room, "Character", test.Character.Id, "arcane-weakness", 2, [], test.Character.Name);
        await test.Db.SaveChangesAsync();

        var (_, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(await test.Db.BattleStatusEffects.ToListAsync(), effect => effect.EffectCode == "talent-holy-heal");
        Assert.DoesNotContain(await test.Db.BattleStatusEffects.ToListAsync(), effect => effect.EffectCode == "talent-holy-damage");
        Assert.DoesNotContain(await test.Db.BattleStatusEffects.ToListAsync(), effect => effect.EffectCode == "arcane-weakness");
        Assert.Single(await test.Db.BattleSkillCooldowns.ToListAsync());
    }

    [Fact]
    [Trait("Category", "TalentBalance")]
    public async Task ActualHealingConsumesHolyHealingEchoAndCreatesDamageEcho()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 50, characterAttack: 1, monsterAttack: 1);
        await PrepareTalentPriestAsync(test, afterglow: false);
        test.Db.CharacterSkillTalents.Add(new() { CharacterId = test.Character.Id, NodeCode = "acolyte-echo", PointsSpent = 1 });
        test.Db.BattleStatusEffects.Add(new()
        {
            RoomId = test.Room.Id, RunSequence = test.Room.RunSequence, TargetType = "Character",
            TargetId = test.Character.Id, EffectCode = "talent-holy-heal", Stacks = 1,
            AppliedRound = test.Room.RoundNumber, ExpiresAfterRound = test.Room.RoundNumber + 3
        });
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "acolyte-heal", autoUse: true);
        var (service, _) = CreateProfessionBalanceService(test);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("恢复 28 点生命值"));
        Assert.DoesNotContain(await test.Db.BattleStatusEffects.ToListAsync(), effect => effect.EffectCode == "talent-holy-heal");
        Assert.Contains(await test.Db.BattleStatusEffects.ToListAsync(), effect => effect.EffectCode == "talent-holy-damage");
    }

    [Theory]
    [Trait("Category", "TalentBalance")]
    [InlineData(false, 0, 1, 3, false)]
    [InlineData(false, 3, 3, 3, false)]
    [InlineData(true, 0, 2, 5, false)]
    [InlineData(true, 1, 3, 5, true)]
    [InlineData(true, 3, 3, 5, true)]
    public async Task RelentlessPoisonExtendsItsOwnApplicationAndBurstsOnlyAtFullStacks(
        bool hasTalent, int existingStacks, int expectedStacks, int expectedDuration, bool expectBurst)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 200, monsterAttack: 1, monsterDefense: 5000);
        await PrepareTalentHunterAsync(test, hasTalent);
        // These multipliers must not amplify the talent's noncritical, defence-ignoring toxin.
        test.Character.WeaponAttackBonusPercent = 100;
        test.Character.TalentSkillDamagePercent = 100;
        test.Character.TalentSkillCriticalChancePercent = 100;
        await test.Db.SaveChangesAsync();
        var (service, monsterCombat) = CreateProfessionBalanceService(test);
        for (var i = 0; i < existingStacks; i++)
            await monsterCombat.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id, "poison", 3, [], test.Monster.Name);
        await test.Db.SaveChangesAsync();
        var applicationRound = test.Room.RoundNumber;

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        var poison = await test.Db.BattleStatusEffects.SingleAsync(effect => effect.EffectCode == "poison");
        Assert.Equal(expectedStacks, poison.Stacks);
        Assert.Equal(applicationRound + expectedDuration, poison.ExpiresAfterRound);
        var bursts = result!.Logs.Where(log => log.Contains("连绵攻势毒蚀")).ToList();
        if (expectBurst)
        {
            var burst = Assert.Single(bursts);
            Assert.Contains("60 点", burst);
            Assert.Contains("无视防御", burst);
            Assert.DoesNotContain("暴击", burst);
        }
        else Assert.Empty(bursts);
    }

    [Fact]
    [Trait("Category", "TalentBalance")]
    public async Task ActivePoisonRefreshFromAnAllyCannotShortenExtendedPoison()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var (_, monsterCombat) = CreateProfessionBalanceService(test);
        await monsterCombat.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id, "poison", 5, [], test.Monster.Name);
        await test.Db.SaveChangesAsync();
        test.Room.RoundNumber = 1;

        await monsterCombat.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id, "poison", 3, [], test.Monster.Name);
        await test.Db.SaveChangesAsync();

        var poison = await test.Db.BattleStatusEffects.SingleAsync();
        Assert.Equal(5, poison.ExpiresAfterRound);
        Assert.Equal(0, poison.AppliedRound);
        Assert.Equal(2, poison.Stacks);
    }

    [Theory]
    [Trait("Category", "TalentBalance")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PoisonAfterCleansingOrExpiryUsesTheNewApplicationsDuration(bool cleanse)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var (_, monsterCombat) = CreateProfessionBalanceService(test);
        await monsterCombat.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id, "poison", 5, [], test.Monster.Name);
        await test.Db.SaveChangesAsync();
        if (cleanse)
            await monsterCombat.RemoveFirstStatusAsync(test.Room, "Monster", [test.Monster.Id], false);
        else
            test.Room.RoundNumber = 6;

        await monsterCombat.ApplyStatusAsync(test.Room, "Monster", test.Monster.Id, "poison", 3, [], test.Monster.Name);
        await test.Db.SaveChangesAsync();

        var poison = await test.Db.BattleStatusEffects.SingleAsync();
        Assert.Equal(test.Room.RoundNumber + 3, poison.ExpiresAfterRound);
        Assert.Equal(test.Room.RoundNumber, poison.AppliedRound);
        Assert.Equal(1, poison.Stacks);
    }

    [Theory]
    [Trait("Category", "TalentBalance")]
    [InlineData("mage", "mage-frost-ward", "mage-frozen-heart")]
    [InlineData("rogue", "rogue-evasion", "rogue-escape-artist")]
    public async Task DefensiveCapstoneCanAutoCleanseWhileAnAllyProvidesStrongerGuard(
        string profession, string skillCode, string talentCode)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 20);
        test.Character.ProfessionCode = profession;
        test.Character.Level = 10;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        var knight = await test.AddSlotAsync(2, "Protector", attack: 1);
        knight.ProfessionCode = "swordsman";
        knight.AdvancedProfessionCode = "knight";
        knight.Level = 10;
        test.Db.CharacterSkillTalents.Add(new() { CharacterId = test.Character.Id, NodeCode = talentCode, PointsSpent = 1 });
        var knightSlot = await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == knight.Id);
        knightSlot.PendingSkillSlotMask = Game.Shared.SkillRules.SlotMask(1);
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(knight, 1, "knight-guard", autoUse: false);
        await test.AddSkillAsync(test.Character, 1, skillCode, autoUse: true);
        await EnableTalentTestAutoAsync(test);
        var (service, monsterCombat) = CreateProfessionBalanceService(test);
        await monsterCombat.ApplyStatusAsync(test.Room, "Character", test.Character.Id, "arcane-weakness", 2, [], test.Character.Name);
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(95, test.Character.Hp);
        Assert.Contains(result!.Logs, log => log.Contains("借助") && log.Contains("移除了"));
        Assert.Equal(2, await test.Db.BattleSkillCooldowns.CountAsync());
    }

    [Fact]
    [Trait("Category", "TalentBalance")]
    public async Task RelentlessPoisonRemainsActiveUntilTheNextVenomArrowIsReady()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        await PrepareTalentHunterAsync(test, hasTalent: true);
        var (service, _) = CreateProfessionBalanceService(test);
        var (first, firstError) = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(firstError);
        Assert.DoesNotContain(first!.Logs, log => log.Contains("连绵攻势毒蚀"));

        for (var i = 0; i < 5; i++)
        {
            test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
            test.Room.Version++;
            await test.Db.SaveChangesAsync();
            var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);
            Assert.Null(error);
            if (i == 4)
                Assert.Single(result!.Logs, log => log.Contains("连绵攻势毒蚀"));
        }
        Assert.Equal(3, (await test.Db.BattleStatusEffects.SingleAsync(effect => effect.EffectCode == "poison")).Stacks);
    }

    private static async Task PrepareTalentPriestAsync(BattleTestContext test, bool afterglow)
    {
        test.Character.ProfessionCode = "acolyte";
        test.Character.AdvancedProfessionCode = "priest";
        test.Character.Level = 10;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        if (afterglow)
            test.Db.CharacterSkillTalents.Add(new() { CharacterId = test.Character.Id, NodeCode = "acolyte-afterglow", PointsSpent = 1 });
        await test.Db.SaveChangesAsync();
        await EnableTalentTestAutoAsync(test);
    }

    private static async Task PrepareTalentHunterAsync(BattleTestContext test, bool hasTalent)
    {
        test.Character.ProfessionCode = "hunter";
        test.Character.Level = 10;
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        test.Db.CharacterSkillTalents.AddRange(
            new CharacterSkillTalent { CharacterId = test.Character.Id, NodeCode = "hunter-steady-hand", PointsSpent = 1 },
            new CharacterSkillTalent { CharacterId = test.Character.Id, NodeCode = "hunter-venom-talent", PointsSpent = 1 });
        if (hasTalent)
            test.Db.CharacterSkillTalents.Add(new() { CharacterId = test.Character.Id, NodeCode = "hunter-relentless", PointsSpent = 1 });
        await test.Db.SaveChangesAsync();
        await test.AddSkillAsync(test.Character, 1, "hunter-venom-arrow", autoUse: true);
        await EnableTalentTestAutoAsync(test);
    }

    private static async Task EnableTalentTestAutoAsync(BattleTestContext test)
    {
        // Talent tests exercise skill Auto inside an explicitly enabled character Auto slot.
        var slot = await test.Db.RoomSlots.SingleAsync(entry => entry.CharacterId == test.Character.Id);
        slot.IsAutoEnabled = true;
        await test.Db.SaveChangesAsync();
    }
}
