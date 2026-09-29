using Game.Server.Services;
using Game.Shared.Dtos;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Theory]
    [InlineData("RoomAuto", false)]
    [InlineData("SkillAuto", false)]
    [InlineData("Unlock", false)]
    [InlineData("Cooldown", false)]
    [InlineData("None", true)]
    public async Task CustomAlwaysStillRequiresAutoSwitchesUnlockAndCooldown(string blockedBy, bool shouldCast)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        await test.AddSkillAsync(test.Character, 1, "knight-strike", autoUse: true);
        var slot = await test.Db.CharacterSkillSlots.SingleAsync();
        slot.AutoConditionOverride = "Always";
        if (blockedBy == "RoomAuto")
        {
            test.Room.IsOwnerAutoEnabled = false;
            (await test.Db.RoomSlots.SingleAsync()).IsAutoEnabled = false;
        }
        if (blockedBy == "SkillAuto") slot.AutoUseEnabled = false;
        if (blockedBy == "Unlock")
            test.Db.CharacterBattleMilestones.RemoveRange(await test.Db.CharacterBattleMilestones.ToListAsync());
        if (blockedBy == "Cooldown")
            test.Db.BattleSkillCooldowns.Add(new BattleSkillCooldown
            {
                RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillCode = "knight-strike", ReadyAtRound = 10
            });
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(shouldCast, result!.Logs.Any(log => log.Contains("使用 盾击")));
        if (blockedBy == "Cooldown")
            Assert.Equal(10, (await test.Db.BattleSkillCooldowns.SingleAsync()).ReadyAtRound);
        else
            Assert.Equal(shouldCast ? 1 : 0, await test.Db.BattleSkillCooldowns.CountAsync());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("InterruptibleIntent", false)]
    [InlineData("Always", true)]
    public async Task AutoDamageInterruptRechecksAfterEarlierManualInterrupt(string? condition, bool shouldCast)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        var (service, _, skill, intent) = await PrepareInterruptSkillAsync(test, "knight-rebuke", "interruptible");
        var second = await test.AddSlotAsync(2, "SecondKnight", attack: 1);
        second.ProfessionCode = test.Character.ProfessionCode;
        second.Level = test.Character.Level;
        foreach (var node in await test.Db.CharacterSkillTalents.Where(node => node.CharacterId == test.Character.Id).ToListAsync())
            test.Db.CharacterSkillTalents.Add(new CharacterSkillTalent
            {
                CharacterId = second.Id, NodeCode = node.NodeCode, PointsSpent = node.PointsSpent
            });
        await test.AddSkillAsync(second, 1, skill.Code, autoUse: true);
        (await test.Db.CharacterSkillSlots.SingleAsync(slot => slot.CharacterId == second.Id)).AutoConditionOverride = condition;
        await test.Db.SaveChangesAsync();
        var (queued, queueError) = await service.QueueSkillAsync(new QueueSkillRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 1, IsQueued = true
        }, test.Token);
        Assert.Null(queueError);
        Assert.True(queued);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.True(intent.IsInterrupted);
        Assert.Single(result!.Logs.Where(log => log.Contains($"使用 {skill.Name}，打断")));
        Assert.Equal(shouldCast, result.Logs.Any(log => log.Contains($"2号位 SecondKnight 使用 {skill.Name} 攻击")));
        Assert.Equal(shouldCast, await test.Db.BattleSkillCooldowns.AnyAsync(cooldown => cooldown.CharacterId == second.Id));
    }

    [Theory]
    [InlineData(null, 709, 140, 2)]
    [InlineData("AllyHpBelowThreshold", 709, 140, 2)]
    [InlineData(null, 699, 139, 2)]
    [InlineData("AllyHpBelowThreshold", 699, 139, 2)]
    [InlineData(null, 700, 140, 1)]
    [InlineData("AllyHpBelowThreshold", 700, 140, 1)]
    public async Task AutoHealingUsesExactHealthRatiosForThresholdAndTarget(
        string? condition, int frontHp, int rearHp, int expectedTargetSlot)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        test.Character.MaxHp = 1000;
        test.Character.Hp = frontHp;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        var rear = await test.AddSlotAsync(2, "Rear", attack: 1);
        rear.MaxHp = 200;
        rear.Hp = rearHp;
        var healer = await test.AddSlotAsync(3, "Healer", attack: 1);
        healer.ProfessionCode = "cleric";
        await test.AddSkillAsync(healer, 1, "cleric-heal", autoUse: true, threshold: 70);
        (await test.Db.CharacterSkillSlots.SingleAsync()).AutoConditionOverride = condition;
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains($"使用 治疗术，为 {expectedTargetSlot}号位"));
        Assert.Equal("cleric-heal", Assert.Single(await test.Db.BattleSkillCooldowns.ToListAsync()).SkillCode);
    }

    [Theory]
    [InlineData("SelfHpBelowThreshold")]
    [InlineData("AllyHpBelowThreshold")]
    public async Task CustomHealthAutoConditionRechecksAfterManualHealing(string condition)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 60, characterAttack: 1, monsterAttack: 1);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        await test.AddSkillAsync(test.Character, 1, "knight-strike", autoUse: true);
        var strike = await test.Db.CharacterSkillSlots.SingleAsync();
        strike.AutoConditionOverride = condition;
        var healer = await test.AddSlotAsync(2, "Healer", attack: 1);
        healer.ProfessionCode = "cleric";
        await test.AddSkillAsync(healer, 1, "cleric-heal", autoUse: false);
        // Manual actions ignore their Auto condition and resolve before all automatic skills.
        var heal = await test.Db.CharacterSkillSlots.SingleAsync(slot => slot.CharacterId == healer.Id);
        heal.AutoConditionOverride = "MonsterHpBelowThreshold";
        heal.AutoHpThresholdPercent = 1;
        await test.Db.SaveChangesAsync();
        var (queued, queueError) = await test.Service.QueueSkillAsync(new QueueSkillRequest
        {
            RoomId = test.Room.Id, CharacterId = healer.Id, SkillSlotIndex = 1, IsQueued = true
        }, test.Token);
        Assert.Null(queueError);
        Assert.True(queued);

        var (result, error) = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("使用 治疗术"));
        Assert.DoesNotContain(result.Logs, log => log.Contains("使用 盾击"));
        Assert.Equal("cleric-heal", Assert.Single(await test.Db.BattleSkillCooldowns.ToListAsync()).SkillCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomMonsterHealthAutoConditionSeesEarlierSkillDamage(bool queueDamage)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 10, monsterAttack: 1, monsterDefense: 0);
        test.Monster.Hp = test.Monster.MaxHp = 100;
        await test.AddSkillAsync(test.Character, 1, "knight-guard", autoUse: true, threshold: 95);
        (await test.Db.CharacterSkillSlots.SingleAsync()).AutoConditionOverride = "MonsterHpBelowThreshold";
        await test.AddSkillAsync(test.Character, 2, "knight-strike", autoUse: false);
        if (queueDamage)
        {
            var (queued, queueError) = await test.Service.QueueSkillAsync(new QueueSkillRequest
            {
                RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 2, IsQueued = true
            }, test.Token);
            Assert.Null(queueError);
            Assert.True(queued);
        }

        var (result, error) = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(queueDamage, result!.Logs.Any(log => log.Contains("使用 守护")));
    }

    [Theory]
    [InlineData("AllyHasDebuff", false)]
    [InlineData("AllyHasDebuff", true)]
    [InlineData("MonsterHasBuff", false)]
    [InlineData("MonsterHasBuff", true)]
    public async Task CustomStatusAutoConditionSeesStatusRemovedEarlierInTheSameRound(string condition, bool removeFirst)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        test.Character.ProfessionCode = "knight";
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        var responder = await test.AddSlotAsync(2, "Responder", attack: 1);
        responder.ProfessionCode = "cleric";
        await test.AddSkillAsync(test.Character, 1, "knight-break", autoUse: true);
        (await test.Db.CharacterSkillSlots.SingleAsync()).AutoConditionOverride = condition;
        var responseCode = condition == "AllyHasDebuff" ? "cleric-purify" : "cleric-dispel";
        await test.AddSkillAsync(responder, 1, responseCode, autoUse: false);
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.CreateResponses();
        var monsters = new MonsterCombatService(test.Db, MonsterCombatTestFactory.CreateCatalog());
        await monsters.ApplyStatusAsync(test.Room, condition == "AllyHasDebuff" ? "Character" : "Monster",
            1, condition == "AllyHasDebuff" ? "poison" : "slime-shell", 2, [], "Target");
        await test.Db.SaveChangesAsync();
        var rewards = RewardTestFactory.CreateService(test.Db, progression);
        var service = new BattleService(test.Db, new UserService(test.Db, progression, skills), ConsumableTestFactory.Create(),
            skills, rewards, new DungeonRunService(test.Db, rewards, monsters), monsters);
        if (removeFirst)
        {
            var (queued, queueError) = await service.QueueSkillAsync(new QueueSkillRequest
            {
                RoomId = test.Room.Id, CharacterId = responder.Id, SkillSlotIndex = 1, IsQueued = true
            }, test.Token);
            Assert.Null(queueError);
            Assert.True(queued);
        }

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(!removeFirst, result!.Logs.Any(log => log.Contains("使用 破甲斩")));
        Assert.Equal(!removeFirst, await test.Db.BattleSkillCooldowns.AnyAsync(cooldown => cooldown.SkillCode == "knight-break"));
    }
}
