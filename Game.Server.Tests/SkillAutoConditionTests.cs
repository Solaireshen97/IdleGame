using Game.Server.Data;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    public static IEnumerable<object?[]> CustomAutoConditions =>
        SkillAutoRules.Conditions.Select(condition => new object?[] { condition }).Append(new object?[] { null });

    [Theory]
    [MemberData(nameof(CustomAutoConditions))]
    public async Task SkillAutoConditionPersistsAcrossSessionsAndAppearsInBothResponses(string? condition)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var settings = MakeSkillSettingsService(test.Db);
        var (equipped, equipError) = await settings.SetSlotAsync(test.Token, 1, 1,
            new SetSkillSlotRequest { SkillCode = "knight-strike", AutoConditionOverride = condition });
        Assert.Null(equipError);
        Assert.Equal(condition, equipped!.Slots[0].AutoConditionOverride);
        var (_, saveError) = await settings.SetAutoAsync(test.Token, 1, 1,
            new SetSkillAutoRequest { AutoUseEnabled = true, AutoConditionOverride = condition, AutoHpThresholdPercent = 37 });
        Assert.Null(saveError);

        await using var freshDb = test.CreateDbContext();
        var (skills, readError) = await MakeSkillSettingsService(freshDb).GetAsync(test.Token, 1);
        Assert.Null(readError);
        var saved = skills!.Slots[0];
        Assert.Equal(condition, saved.AutoConditionOverride);
        Assert.Equal(condition ?? "Always", saved.AutoCondition);
        Assert.True(saved.AutoUseEnabled);
        Assert.Equal(37, saved.AutoHpThresholdPercent);
        var progression = ProgressionTestFactory.Create();
        var catalog = SkillTestFactory.Create();
        var rooms = new RoomService(freshDb, new UserService(freshDb, progression, catalog), progression,
            ConsumableTestFactory.Create(), catalog, RewardTestFactory.CreateService(freshDb, progression));
        var roomSkill = (await rooms.GetRoomDetailAsync(test.Room.Id, test.Token))!.Slots[0].Skills[0];
        Assert.Equal(condition, roomSkill.AutoConditionOverride);
        Assert.Equal(condition ?? "Always", roomSkill.AutoCondition);
        Assert.Equal(37, roomSkill.AutoHpThresholdPercent);
    }

    [Fact]
    public async Task RestoringDefaultAutoConditionKeepsTheSkillDefaultAndThreshold()
    {
        await using var test = await BattleTestContext.CreateAsync();
        await test.AddSkillAsync(test.Character, 1, "knight-guard", autoUse: false);
        var settings = MakeSkillSettingsService(test.Db);
        await settings.SetAutoAsync(test.Token, 1, 1,
            new SetSkillAutoRequest { AutoConditionOverride = "Always", AutoUseEnabled = true, AutoHpThresholdPercent = 45 });

        var (response, error) = await settings.SetAutoAsync(test.Token, 1, 1,
            new SetSkillAutoRequest { AutoConditionOverride = null, AutoUseEnabled = true, AutoHpThresholdPercent = 45 });

        Assert.Null(error);
        Assert.Null(response!.Slots[0].AutoConditionOverride);
        Assert.Equal("LowestHpBelowThreshold", response.Slots[0].AutoCondition);
        Assert.Equal(45, response.Slots[0].AutoHpThresholdPercent);
        Assert.Null((await test.Db.CharacterSkillSlots.SingleAsync()).AutoConditionOverride);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Always && SelfHpBelowThreshold")]
    [InlineData("!MonsterHasBuff")]
    public async Task InvalidOrCombinedAutoConditionsAreRejectedWithoutChangingSettings(string condition)
    {
        await using var test = await BattleTestContext.CreateAsync();
        await test.AddSkillAsync(test.Character, 1, "knight-strike", autoUse: false);
        var settings = MakeSkillSettingsService(test.Db);
        var slot = await test.Db.CharacterSkillSlots.SingleAsync();
        var originalVersion = slot.Version;

        var (auto, autoError) = await settings.SetAutoAsync(test.Token, 1, 1,
            new SetSkillAutoRequest { AutoConditionOverride = condition, AutoUseEnabled = true });
        var (equipment, equipmentError) = await settings.SetSlotAsync(test.Token, 1, 1,
            new SetSkillSlotRequest { SkillCode = "knight-guard", AutoConditionOverride = condition });

        Assert.Null(auto);
        Assert.Null(equipment);
        Assert.Equal("InvalidAutoCondition", autoError);
        Assert.Equal("InvalidAutoCondition", equipmentError);
        Assert.Equal("knight-strike", slot.SkillCode);
        Assert.Null(slot.AutoConditionOverride);
        Assert.False(slot.AutoUseEnabled);
        Assert.Equal(originalVersion, slot.Version);
    }

    [Fact]
    public async Task AnotherAccountCannotChangeSkillAutoCondition()
    {
        await using var test = await BattleTestContext.CreateAsync();
        await test.AddOtherMemberAsync();
        await test.AddSkillAsync(test.Character, 1, "knight-strike", autoUse: false);

        var (response, error) = await MakeSkillSettingsService(test.Db).SetAutoAsync("other-token", 1, 1,
            new SetSkillAutoRequest { AutoUseEnabled = true, AutoConditionOverride = "Always" });

        Assert.Null(response);
        Assert.NotNull(error);
        Assert.Null((await test.Db.CharacterSkillSlots.SingleAsync()).AutoConditionOverride);
    }

    [Fact]
    public async Task SkillAutoConditionCanChangeDuringBattleWithoutChangingEquipmentOrQueuedActions()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 42);
        await test.AddSkillAsync(test.Character, 1, "knight-strike", autoUse: false);
        var roomSlot = await test.Db.RoomSlots.SingleAsync();
        roomSlot.PendingSkillSlotMask = SkillRules.SlotMask(1);
        test.Room.Status = RoomStatus.Preparing;
        await test.Db.SaveChangesAsync();

        var (response, error) = await MakeSkillSettingsService(test.Db).SetAutoAsync(test.Token, 1, 1,
            new SetSkillAutoRequest { AutoUseEnabled = true, AutoConditionOverride = "MonsterHpBelowThreshold", AutoHpThresholdPercent = 35 });

        Assert.Null(error);
        Assert.Equal("MonsterHpBelowThreshold", response!.Slots[0].AutoCondition);
        Assert.Equal("knight-strike", response.Slots[0].SkillCode);
        Assert.Equal(SkillRules.SlotMask(1), roomSlot.PendingSkillSlotMask);
        Assert.Equal(42, test.Character.Hp);
        Assert.Equal(RoomStatus.Preparing, test.Room.Status);
        Assert.False(test.Room.IsOwnerAutoEnabled);
    }

    [Fact]
    public async Task AutoConditionFollowsSkillWhenSwappedAndIsClearedWhenUnequipped()
    {
        await using var test = await BattleTestContext.CreateAsync();
        test.Character.ProfessionCode = "knight";
        test.Character.Level = 3;
        var settings = MakeSkillSettingsService(test.Db);
        var (_, firstError) = await settings.SetSlotAsync(test.Token, 1, 1, new SetSkillSlotRequest
            { SkillCode = "knight-strike", AutoUseEnabled = true, AutoConditionOverride = "MonsterHpBelowThreshold", AutoHpThresholdPercent = 35 });
        var (_, secondError) = await settings.SetSlotAsync(test.Token, 1, 2, new SetSkillSlotRequest
            { SkillCode = "knight-guard", AutoUseEnabled = false, AutoConditionOverride = "SelfHpBelowThreshold", AutoHpThresholdPercent = 65 });
        Assert.Null(firstError);
        Assert.Null(secondError);

        var (swapped, error) = await settings.SwapSlotsAsync(test.Token, 1,
            new SwapSkillSlotsRequest { FromSlotIndex = 1, ToSlotIndex = 2 });

        Assert.Null(error);
        Assert.Equal(("knight-guard", "SelfHpBelowThreshold", 65, false),
            (swapped!.Slots[0].SkillCode, swapped.Slots[0].AutoConditionOverride, swapped.Slots[0].AutoHpThresholdPercent, swapped.Slots[0].AutoUseEnabled));
        Assert.Equal(("knight-strike", "MonsterHpBelowThreshold", 35, true),
            (swapped.Slots[1].SkillCode, swapped.Slots[1].AutoConditionOverride, swapped.Slots[1].AutoHpThresholdPercent, swapped.Slots[1].AutoUseEnabled));
        var (unequipped, unequipError) = await settings.SetSlotAsync(test.Token, 1, 1,
            new SetSkillSlotRequest { SkillCode = null, AutoConditionOverride = "Always" });
        Assert.Null(unequipError);
        Assert.Null(unequipped!.Slots[0].AutoConditionOverride);
        Assert.Null((await test.Db.CharacterSkillSlots.SingleAsync(slot => slot.SlotIndex == 1)).AutoConditionOverride);
        Assert.Equal("MonsterHpBelowThreshold", unequipped.Slots[1].AutoConditionOverride);
    }

    [Theory]
    [InlineData("Always", 100, 1000, 70, true)]
    [InlineData("SelfHpBelowThreshold", 70, 1000, 70, true)]
    [InlineData("SelfHpBelowThreshold", 71, 1000, 70, false)]
    [InlineData("SelfHpBelowThreshold", 1, 1000, 1, true)]
    [InlineData("SelfHpBelowThreshold", 100, 1000, 100, true)]
    [InlineData("MonsterHpBelowThreshold", 100, 400, 40, true)]
    [InlineData("MonsterHpBelowThreshold", 100, 401, 40, false)]
    public async Task CustomAutoHealthConditionUsesTheSelectedTargetAndInclusiveThreshold(
        string condition, int selfHp, int monsterHp, int threshold, bool shouldCast)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: selfHp, characterAttack: 1, monsterAttack: 1);
        test.Monster.Hp = monsterHp;
        test.Monster.MaxHp = 1000;
        await test.AddSkillAsync(test.Character, 1, "knight-strike", autoUse: true);
        Assert.Null((await MakeSkillSettingsService(test.Db).SetAutoAsync(test.Token, 1, 1,
            new SetSkillAutoRequest { AutoUseEnabled = true, AutoConditionOverride = condition, AutoHpThresholdPercent = threshold })).Error);

        var (result, error) = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(shouldCast, result!.Logs.Any(log => log.Contains("使用 盾击")));
        Assert.Equal(shouldCast ? 1 : 0, await test.Db.BattleSkillCooldowns.CountAsync());
    }

    [Theory]
    [InlineData("AllyHpBelowThreshold", 100, 70, true)]
    [InlineData("AllyHpBelowThreshold", 100, 71, false)]
    [InlineData("AllyHpBelowThreshold", 70, 100, true)]
    [InlineData("AllyHpBelowThreshold", 100, 0, false)]
    [InlineData("FrontAllyHpBelowThreshold", 100, 70, true)]
    [InlineData("FrontAllyHpBelowThreshold", 70, 100, false)]
    [InlineData("FrontAllyHpBelowThreshold", 100, 0, false)]
    public async Task CustomAllyHealthConditionsCheckLivingAlliesAndFrontPosition(string condition, int selfHp, int allyHp, bool shouldCast)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: selfHp, characterAttack: 1, monsterAttack: 1);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        (await test.Db.RoomSlots.SingleAsync()).SlotIndex = 2;
        await test.Db.SaveChangesAsync();
        await test.AddSlotAsync(1, "Front", hp: allyHp, attack: 1);
        await test.AddSkillAsync(test.Character, 1, "knight-strike", autoUse: true);
        await MakeSkillSettingsService(test.Db).SetAutoAsync(test.Token, 1, 1,
            new SetSkillAutoRequest { AutoUseEnabled = true, AutoConditionOverride = condition, AutoHpThresholdPercent = 70 });

        var (result, error) = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(shouldCast, result!.Logs.Any(log => log.Contains("使用 盾击")));
    }

    [Theory]
    [InlineData("AllyHasDebuff", true)]
    [InlineData("AllyHasDebuff", false)]
    [InlineData("MonsterHasBuff", true)]
    [InlineData("MonsterHasBuff", false)]
    [InlineData("InterruptibleIntent", true)]
    [InlineData("InterruptibleIntent", false)]
    public async Task CustomAutoStatusConditionAppliesToDamageSkills(string condition, bool triggerPresent)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        if (condition == "InterruptibleIntent" && triggerPresent) test.Monster.CombatProfileCode = "acid-slime";
        await test.AddSkillAsync(test.Character, 1, "knight-strike", autoUse: true);
        await MakeSkillSettingsService(test.Db).SetAutoAsync(test.Token, 1, 1,
            new SetSkillAutoRequest { AutoUseEnabled = true, AutoConditionOverride = condition });
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        var monsters = new MonsterCombatService(test.Db, MonsterCombatTestFactory.CreateCatalog());
        if (triggerPresent && condition == "AllyHasDebuff")
            await monsters.ApplyStatusAsync(test.Room, "Character", 1, "poison", 2, [], "Knight");
        if (triggerPresent && condition == "MonsterHasBuff")
            await monsters.ApplyStatusAsync(test.Room, "Monster", 1, "slime-shell", 2, [], "Slime");
        await test.Db.SaveChangesAsync();
        var rewards = RewardTestFactory.CreateService(test.Db, progression);
        var service = new BattleService(test.Db, new UserService(test.Db, progression, skills), ConsumableTestFactory.Create(),
            skills, rewards, new DungeonRunService(test.Db, rewards, monsters), monsters);

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(triggerPresent, result!.Logs.Any(log => log.Contains("使用 盾击")));
    }

    [Theory]
    [InlineData("hunter-rapid-volley")]
    [InlineData("rogue-gouge")]
    public async Task CustomAlwaysAllowsDamageInterruptAutoWithoutInterruptibleIntent(string skillCode)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        var (service, _, skill, _) = await PrepareInterruptSkillAsync(test, skillCode, "basic", autoUse: true);
        (await test.Db.CharacterSkillSlots.SingleAsync()).AutoConditionOverride = "Always";
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Contains(result!.Logs, log => log.Contains($"使用 {skill.Name} 攻击"));
        Assert.Equal(skillCode, Assert.Single(await test.Db.BattleSkillCooldowns.ToListAsync()).SkillCode);
    }

    [Theory]
    [InlineData("acolyte-silence")]
    public async Task CustomAlwaysCannotBypassNonDamageInterruptRequirements(string skillCode)
    {
        await using var test = await BattleTestContext.CreateAsync();
        var (service, _, skill, _) = await PrepareInterruptSkillAsync(test, skillCode, "basic", autoUse: true);
        (await test.Db.CharacterSkillSlots.SingleAsync()).AutoConditionOverride = "Always";
        await test.Db.SaveChangesAsync();

        var (result, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.DoesNotContain(result!.Logs, log => log.Contains($"使用 {skill.Name}"));
        Assert.Empty(await test.Db.BattleSkillCooldowns.ToListAsync());
    }

    [Theory]
    [InlineData("mage", "mage-frost-ward", "mage-frozen-heart", false)]
    [InlineData("mage", "mage-frost-ward", "mage-frozen-heart", true)]
    [InlineData("rogue", "rogue-evasion", "rogue-escape-artist", false)]
    [InlineData("rogue", "rogue-evasion", "rogue-escape-artist", true)]
    public async Task CustomHealthConditionDoesNotAddTheDefaultSelfCleanseTrigger(string profession, string skillCode, string talent, bool custom)
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        test.Character.ProfessionCode = profession;
        test.Character.Level = 10;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        test.Db.CharacterSkillTalents.Add(new CharacterSkillTalent { CharacterId = 1, NodeCode = talent, PointsSpent = 1 });
        await test.AddSkillAsync(test.Character, 1, skillCode, autoUse: true, threshold: 70);
        if (custom) (await test.Db.CharacterSkillSlots.SingleAsync()).AutoConditionOverride = "SelfHpBelowThreshold";
        var (service, monsters) = CreateProductionSoulBattleService(test);
        await monsters.ApplyStatusAsync(test.Room, "Character", 1, "poison", 2, [], "Knight");
        await test.Db.SaveChangesAsync();

        var (_, error) = await service.StartPreparationAsync(test.Room.Id, test.Token);

        Assert.Null(error);
        Assert.Equal(custom ? 0 : 1, await test.Db.BattleSkillCooldowns.CountAsync());
        Assert.Equal(custom, await test.Db.BattleStatusEffects.AnyAsync(effect => effect.TargetType == "Character" && effect.TargetId == 1));
    }

    private static SkillService MakeSkillSettingsService(GameDbContext db)
    {
        var catalog = SkillTestFactory.Create();
        return new SkillService(db, new UserService(db, ProgressionTestFactory.Create(), catalog), catalog);
    }
}
