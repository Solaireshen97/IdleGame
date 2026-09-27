using Game.Server.Data;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Dtos.Characters;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    [Theory]
    [InlineData("Self")]
    [InlineData("Ally")]
    [InlineData("Guest")]
    public async Task ManualHealUsesChosenCharacterAcrossRequestsInsteadOfLowestHealth(string selection)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 80, characterAttack: 1, monsterAttack: 1);
        test.Character.ProfessionCode = "cleric";
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        Character target;
        if (selection == "Guest")
        {
            var guest = await test.AddOtherMemberAsync();
            guest.IsConfirmed = true;
            target = await test.Db.Characters.SingleAsync(character => character.Id == guest.CharacterId);
            target.Hp = 80;
        }
        else target = selection == "Self" ? test.Character : await test.AddSlotAsync(2, "Chosen", hp: 80, attack: 1);
        await test.AddSlotAsync(3, "Lowest", hp: 20, attack: 1);
        await test.AddSkillAsync(test.Character, 1, "cleric-heal", autoUse: false);
        var request = new QueueSkillRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 1, IsQueued = true,
            TargetCharacterId = target.Id
        };
        var (queued, queueError) = await test.Service.QueueSkillAsync(request, test.Token);
        Assert.Null(queueError);
        Assert.True(queued);
        var detail = await test.GetRoomDetailAsync();
        Assert.Equal(target.Id, detail!.Slots[0].Skills[0].QueuedTargetCharacterId);

        await using var fresh = test.CreateDbContext();
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        var service = new BattleService(fresh, new UserService(fresh, progression, skills), ConsumableTestFactory.Create(),
            skills, RewardTestFactory.CreateService(fresh, progression));
        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        var healLog = Assert.Single(result!.Logs.Where(log => log.Contains("使用 治疗术")));
        Assert.Contains($"{target.Name} 恢复", healLog);
        Assert.DoesNotContain("Lowest 恢复", healLog);
        Assert.Equal("cleric-heal", Assert.Single(await fresh.BattleSkillCooldowns.ToListAsync()).SkillCode);
        Assert.All(await fresh.RoomSlots.ToListAsync(), slot => Assert.Null(slot.PendingSkillTargetsJson));
    }

    [Theory]
    [InlineData("Healed")]
    [InlineData("Dead")]
    [InlineData("Left")]
    public async Task LostManualHealTargetSkipsWithoutCooldownOrRetargetAndNextAutoRemainsSmart(string change)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        test.Character.ProfessionCode = "cleric";
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        var chosen = await test.AddSlotAsync(2, "Chosen", hp: 80, attack: 1);
        var lowest = await test.AddSlotAsync(3, "Lowest", hp: 20, attack: 1);
        await test.AddSkillAsync(test.Character, 1, "cleric-heal", autoUse: true);
        Assert.True((await test.Service.QueueSkillAsync(new QueueSkillRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 1, IsQueued = true,
            TargetCharacterId = chosen.Id
        }, test.Token)).Success);
        if (change == "Healed") chosen.Hp = 100;
        if (change == "Dead") chosen.Hp = 0;
        if (change == "Left")
        {
            var position = await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == chosen.Id);
            position.CharacterId = null;
            position.UserId = null;
        }
        await test.Db.SaveChangesAsync();

        var (result, error) = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.DoesNotContain(result!.Logs, log => log.Contains("使用 治疗术"));
        Assert.Empty(await test.Db.BattleSkillCooldowns.ToListAsync());
        Assert.Equal(20, lowest.Hp);
        Assert.All(await test.Db.RoomSlots.ToListAsync(), slot => Assert.Null(slot.PendingSkillTargetsJson));

        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        var (next, nextError) = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(nextError);
        Assert.Contains(next!.Logs, log => log.Contains("使用 治疗术，为 3号位 Lowest"));
    }

    [Fact]
    public async Task ManualHealFollowsChosenCharacterWhenPartyPositionsChange()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        test.Character.ProfessionCode = "cleric";
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        var chosen = await test.AddSlotAsync(2, "Chosen", hp: 80, attack: 1);
        await test.AddSlotAsync(3, "Lowest", hp: 20, attack: 1);
        await test.AddSkillAsync(test.Character, 1, "cleric-heal", autoUse: false);
        Assert.True((await test.Service.QueueSkillAsync(new QueueSkillRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 1, IsQueued = true,
            TargetCharacterId = chosen.Id
        }, test.Token)).Success);
        (await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == chosen.Id)).SlotIndex = 4;
        await test.AddSlotAsync(2, "Replacement", hp: 10, attack: 1);

        var (result, error) = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("使用 治疗术，为 4号位 Chosen"));
        Assert.DoesNotContain(result.Logs, log => log.Contains("Replacement 恢复"));
    }

    [Fact]
    public async Task ManualGuardProtectsChosenAllyInsteadOfFront()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 10);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        var chosen = await test.AddSlotAsync(2, "Chosen", hp: 80, attack: 1);
        await test.AddSkillAsync(test.Character, 1, "knight-guard", autoUse: false);
        Assert.True((await test.Service.QueueSkillAsync(new QueueSkillRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 1, IsQueued = true,
            TargetCharacterId = chosen.Id
        }, test.Token)).Success);

        var (result, error) = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("使用 守护，守护 2号位 Chosen"));
        Assert.Equal(90, test.Character.Hp);
    }

    [Fact]
    public async Task ManualCleanseOnlyRemovesChosenAllysDebuff()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        test.Character.ProfessionCode = "cleric";
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        var chosen = await test.AddSlotAsync(2, "Chosen", attack: 1);
        await test.AddSkillAsync(test.Character, 1, "cleric-purify", autoUse: false);
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.CreateResponses();
        var monsters = new MonsterCombatService(test.Db, MonsterCombatTestFactory.CreateCatalog());
        foreach (var character in new[] { test.Character, chosen })
            await monsters.ApplyStatusAsync(test.Room, "Character", character.Id, "poison", 2, [], character.Name);
        await test.Db.SaveChangesAsync();
        var rewards = RewardTestFactory.CreateService(test.Db, progression);
        var service = new BattleService(test.Db, new UserService(test.Db, progression, skills), ConsumableTestFactory.Create(),
            skills, rewards, new DungeonRunService(test.Db, rewards, monsters), monsters);
        Assert.True((await service.QueueSkillAsync(new QueueSkillRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 1, IsQueued = true,
            TargetCharacterId = chosen.Id
        }, test.Token)).Success);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("移除了 2号位 Chosen"));
        Assert.True(await monsters.HasRemovableStatusAsync(test.Room, "Character", [test.Character.Id], false));
        Assert.False(await monsters.HasRemovableStatusAsync(test.Room, "Character", [chosen.Id], false));
    }

    [Theory]
    [InlineData("Missing", "InvalidSkillTarget")]
    [InlineData("OutsideRoom", "InvalidSkillTarget")]
    [InlineData("Dead", "InvalidSkillTarget")]
    [InlineData("FullHealth", "NoValidSkillTarget")]
    public async Task ManualHealRejectsInvalidTargetWithoutChangingQueue(string kind, string expectedError)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 20);
        test.Character.ProfessionCode = "cleric";
        var other = await test.AddSlotAsync(2, "Other", hp: kind == "Dead" ? 0 : 100);
        if (kind == "OutsideRoom") test.Db.RoomSlots.Remove(await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == other.Id));
        await test.AddSkillAsync(test.Character, 1, "cleric-heal", autoUse: false);

        var (success, error) = await test.Service.QueueSkillAsync(new QueueSkillRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 1, IsQueued = true,
            TargetCharacterId = kind == "Missing" ? 9999 : other.Id
        }, test.Token);

        Assert.False(success);
        Assert.Equal(expectedError, error);
        var slot = await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == test.Character.Id);
        Assert.Equal(0, slot.PendingSkillSlotMask);
        Assert.Null(slot.PendingSkillTargetsJson);
    }

    [Theory]
    [InlineData("hunter", null, "hunter-field-mend")]
    [InlineData("mage", null, "mage-frost-ward")]
    [InlineData("acolyte", "priest", "priest-group-heal")]
    [InlineData("swordsman", null, "sword-slash")]
    public async Task FixedScopeSkillsRejectManualAllyOverride(string profession, string? promotion, string code)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 50);
        test.Character.ProfessionCode = profession;
        test.Character.AdvancedProfessionCode = promotion;
        test.Character.Level = 10;
        await test.AddSkillAsync(test.Character, 1, code, autoUse: false);
        var (service, _) = CreateProductionSoulBattleService(test);

        var (success, error) = await service.QueueSkillAsync(new QueueSkillRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 1, IsQueued = true,
            TargetCharacterId = test.Character.Id
        }, test.Token);

        Assert.False(success);
        Assert.Equal("InvalidSkillTarget", error);
    }

    [Fact]
    public async Task CancellingOneQueuedTargetPreservesOthersAndRequeueDoesNotReuseOldTarget()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 30, characterAttack: 1, monsterAttack: 1);
        test.Character.ProfessionCode = "cleric";
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        var chosen = await test.AddSlotAsync(2, "Chosen", hp: 80, attack: 1);
        test.Db.CharacterSkillTalents.Add(new CharacterSkillTalent { CharacterId = test.Character.Id, NodeCode = "cleric-light", PointsSpent = 1 });
        await test.AddSkillAsync(test.Character, 1, "cleric-heal", autoUse: false);
        await test.AddSkillAsync(test.Character, 2, "cleric-blessing", autoUse: false);
        foreach (var index in new[] { 1, 2 })
            Assert.True((await test.Service.QueueSkillAsync(new QueueSkillRequest
            {
                RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = index, IsQueued = true,
                TargetCharacterId = chosen.Id
            }, test.Token)).Success);
        Assert.True((await test.Service.QueueSkillAsync(new QueueSkillRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 1, IsQueued = false
        }, test.Token)).Success);
        var roomSlot = await test.Db.RoomSlots.SingleAsync(slot => slot.CharacterId == test.Character.Id);
        Assert.Null(SkillQueueRules.TargetCharacterId(roomSlot, 1));
        Assert.Equal(chosen.Id, SkillQueueRules.TargetCharacterId(roomSlot, 2));
        Assert.True((await test.Service.QueueSkillAsync(new QueueSkillRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 1, IsQueued = true,
            TargetCharacterId = test.Character.Id
        }, test.Token)).Success);

        var (result, error) = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains("使用 治疗术，为 1号位 Knight"));
        Assert.Contains(result.Logs, log => log.Contains("使用 祈福，为 2号位 Chosen"));
    }

    [Fact]
    public async Task AnotherAccountCannotQueueTargetsForMyCharacter()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 40);
        test.Character.ProfessionCode = "cleric";
        await test.AddOtherMemberAsync();
        await test.AddSkillAsync(test.Character, 1, "cleric-heal", autoUse: false);

        var (success, error) = await test.Service.QueueSkillAsync(new QueueSkillRequest
        {
            RoomId = test.Room.Id, CharacterId = test.Character.Id, SkillSlotIndex = 1, IsQueued = true,
            TargetCharacterId = test.Character.Id
        }, "other-token");

        Assert.False(success);
        Assert.Equal("NotCharacterOwner", error);
    }
}

public class ManualSkillTargetMigrationTests
{
    [Fact]
    public async Task MigrationKeepsExistingQueuedSkillsWithoutChoosingAnArbitraryTarget()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-manual-target-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options);
            await db.Database.GetService<IMigrator>().MigrateAsync("20260927040000_AddSkillAutoCondition");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO RoomSlots (RoomId, SlotIndex, CharacterId, UserId, PendingSkillSlotMask)
                VALUES (1, 1, 1, 1, 3);
                """);

            await db.Database.MigrateAsync();

            var slot = await db.RoomSlots.SingleAsync();
            Assert.Equal(3, slot.PendingSkillSlotMask);
            Assert.Null(slot.PendingSkillTargetsJson);
            Assert.Null(SkillQueueRules.TargetCharacterId(slot, 1));
            Assert.False(db.Database.HasPendingModelChanges());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
