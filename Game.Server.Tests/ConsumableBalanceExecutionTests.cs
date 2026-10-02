using Game.Server.Configuration;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    private static (BattleService Battle, ConsumableService Supplies, ConsumableCatalog Catalog) ProductionPotionServices(BattleTestContext test)
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        var weapons = new WeaponCatalog(Options.Create(configuration.GetSection(WeaponOptions.SectionName).Get<WeaponOptions>()!));
        var catalog = new ConsumableCatalog(Options.Create(configuration.GetSection(ConsumableOptions.SectionName).Get<ConsumableOptions>()!), weapons);
        var progression = ProgressionTestFactory.Create();
        var skills = SkillTestFactory.Create();
        var users = new UserService(test.Db, progression, skills);
        return (new BattleService(test.Db, users, catalog, skills, RewardTestFactory.CreateService(test.Db, progression), weaponCatalog: weapons),
            new ConsumableService(test.Db, users, catalog), catalog);
    }

    [Theory]
    [InlineData("Always", true)]
    [InlineData("SelfHpBelowThreshold", true)]
    [InlineData("AllyHpBelowThreshold", true)]
    [InlineData("FrontAllyHpBelowThreshold", true)]
    [InlineData("MonsterHpBelowThreshold", false)]
    [InlineData("AllyHasDebuff", false)]
    [InlineData("MonsterHasBuff", false)]
    [InlineData("InterruptibleIntent", false)]
    [InlineData("PreferInterrupt", true)]
    public async Task HealingAndBuffPotionsUseTheSharedAutoConditionSnapshot(string condition, bool shouldUse)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 20, characterAttack: 1, monsterAttack: 1);
        await test.AddPotionAsync(test.Character, 3, true, 40);
        test.Db.AddRange(new CharacterItemStack { CharacterId = test.Character.Id, ItemCode = "whetstone-oil", Quantity = 3 },
            new CharacterConsumableSlot { CharacterId = test.Character.Id, SlotIndex = 2, ItemCode = "whetstone-oil",
                AutoUseEnabled = true, AutoHpThresholdPercent = 40 });
        foreach (var slot in test.Db.CharacterConsumableSlots.Local) slot.AutoConditionOverride = condition;
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        var services = ProductionPotionServices(test);
        Assert.Null((await services.Battle.StartPreparationAsync(test.Room.Id, test.Token)).Error);
        Assert.All(await test.Db.CharacterItemStacks.ToListAsync(), stack => Assert.Equal(shouldUse ? 2 : 3, stack.Quantity));
        var uses = await test.Db.BattleHealingPotionStates.SingleOrDefaultAsync();
        Assert.Equal(shouldUse ? 1 : 0, uses?.UsesUsed ?? 0);
        Assert.Equal(shouldUse ? 1 : 0, uses?.BuffUsesUsed ?? 0);
        if (shouldUse) Assert.Equal(49, test.Character.Hp); // 30% of 100, then the enemy's 1 damage.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuffQuotaPersistsAcrossWavesRejoinRestockAndResetsOnlyForNewRun(bool automatic)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 20, characterAttack: 1, monsterAttack: 1);
        await test.AddPotionAsync(test.Character, 4, false);
        var battle = await AddCombatBuffAsync(test, automatic);
        async Task AdvanceToAsync(int round)
        {
            while (test.Room.RoundNumber < round)
            {
                test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
                await test.Db.SaveChangesAsync();
                Assert.Null((await battle.StartPreparationAsync(test.Room.Id, test.Token)).Error);
            }
        }
        for (var use = 0; use < 2; use++)
        {
            await AdvanceToAsync(use * 10);
            test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await test.Db.SaveChangesAsync();
            if (!automatic) Assert.True((await battle.QueueConsumableAsync(PotionRequest(test, 2), test.Token)).Success);
            Assert.Null((await battle.StartPreparationAsync(test.Room.Id, test.Token)).Error);
            Assert.Equal(use + 1, (await test.Db.BattleHealingPotionStates.SingleAsync()).BuffUsesUsed);
            var buff = await test.Db.BattleConsumableBuffs.OrderByDescending(buff => buff.AppliedRound).FirstAsync();
            Assert.Equal((6, use * 10 + 5), (buff.SkillLevel, buff.ExpiresAfterRound));
            Assert.Equal(use * 10 + 10, (await test.Db.BattleConsumableCooldowns.SingleAsync()).ReadyAtRound);
            test.Character.Hp = 60;
        }
        var endedRun = test.Room.RunSequence;
        test.Db.RoomSlots.Remove(await test.Db.RoomSlots.SingleAsync());
        await test.Db.SaveChangesAsync();
        test.Db.RoomSlots.Add(new RoomSlot { RoomId = test.Room.Id, SlotIndex = 1, UserId = 1, CharacterId = test.Character.Id });
        (await test.Db.CharacterItemStacks.SingleAsync(stack => stack.ItemCode == "whetstone-oil")).Quantity = 9;
        test.Room.CurrentWaveNumber++;
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        await AdvanceToAsync(20);
        await using (var fresh = test.CreateDbContext())
            Assert.Equal(2, (await fresh.BattleHealingPotionStates.SingleAsync()).BuffUsesUsed);
        Assert.Equal("BuffPotionLimitReached", (await battle.QueueConsumableAsync(PotionRequest(test, 2), test.Token)).Error);
        Assert.Null((await battle.StartPreparationAsync(test.Room.Id, test.Token)).Error);
        Assert.Equal(9, (await test.Db.CharacterItemStacks.SingleAsync(stack => stack.ItemCode == "whetstone-oil")).Quantity);
        var progression = ProgressionTestFactory.Create();
        var detail = await new RoomService(test.Db, new UserService(test.Db, progression, SkillTestFactory.Create()), progression,
            ProductionPotionServices(test).Catalog, SkillTestFactory.Create(), RewardTestFactory.CreateService(test.Db, progression))
            .GetRoomDetailAsync(test.Room.Id, test.Token);
        Assert.Equal((2, 0, 2), (detail!.Slots.Single().BuffPotionUsesUsed, detail.Slots.Single().BuffPotionUsesRemaining, detail.Slots.Single().BuffPotionUsesLimit));
        Assert.Equal("BuffPotionLimitReached", detail.Slots.Single().Consumables.Single(slot => slot.SlotIndex == 2).UnavailableReason);
        Assert.True((await battle.QueueConsumableAsync(PotionRequest(test, 1), test.Token)).Success);
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        Assert.Null((await battle.StartPreparationAsync(test.Room.Id, test.Token)).Error);
        Assert.Equal((1, 2), ((await test.Db.BattleHealingPotionStates.SingleAsync()).UsesUsed,
            (await test.Db.BattleHealingPotionStates.SingleAsync()).BuffUsesUsed));
        test.Monster.Hp = 1;
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        Assert.Null((await battle.StartPreparationAsync(test.Room.Id, test.Token)).Error);
        Assert.True((await battle.ResetBattleAsync(test.Room.Id, test.Token)).Success);
        Assert.True(test.Room.RunSequence > endedRun);
        Assert.True((await battle.QueueConsumableAsync(PotionRequest(test, 2), test.Token)).Success);
        Assert.Null((await battle.StartPreparationAsync(test.Room.Id, test.Token)).Error);
        Assert.Equal(1, (await test.Db.BattleHealingPotionStates.SingleAsync(state => state.RunSequence == test.Room.RunSequence)).BuffUsesUsed);
    }

    [Fact]
    public async Task RoomPotionPolicyIsIsolatedFromSavedLoadoutAndManualUseBypassesCondition()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 20, characterAttack: 1, monsterAttack: 1);
        await test.AddPotionAsync(test.Character, 3, true, 40);
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        var services = ProductionPotionServices(test);
        var result = await services.Supplies.SetAutoAsync(test.Token, test.Character.Id, 1, new()
            { AutoUseEnabled = true, AutoConditionOverride = "MonsterHpBelowThreshold", AutoHpThresholdPercent = 10 });
        Assert.Null(result.Error);
        Assert.Equal("MonsterHpBelowThreshold", result.Response!.Slots.Single(slot => slot.SlotIndex == 1).AutoCondition);
        var saved = await test.Db.CharacterConsumableSlots.SingleAsync();
        Assert.Null(saved.AutoConditionOverride);
        Assert.Equal(40, saved.AutoHpThresholdPercent);
        Assert.Null((await services.Battle.StartPreparationAsync(test.Room.Id, test.Token)).Error);
        Assert.Equal(3, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Empty(await test.Db.BattleHealingPotionStates.ToListAsync());
        Assert.True((await services.Battle.QueueConsumableAsync(PotionRequest(test), test.Token)).Success);
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        Assert.Null((await services.Battle.StartPreparationAsync(test.Room.Id, test.Token)).Error);
        Assert.Equal(2, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
        var roomVersion = test.Room.Version;
        Assert.Equal("InvalidAutoCondition", (await services.Supplies.SetAutoAsync(test.Token, test.Character.Id, 1,
            new() { AutoUseEnabled = true, AutoConditionOverride = "unsupported" })).Error);
        Assert.Equal(roomVersion, test.Room.Version);
    }

    [Theory]
    [InlineData("lesser-minor-healing-potion", 20)]
    [InlineData("minor-healing-potion", 30)]
    public async Task HealingPotionPreviewAndRealHealingUseOnlyMaximumHpPercent(string code, int percent)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 20, characterAttack: 1, monsterAttack: 1);
        await test.AddPotionAsync(test.Character, 3, true, 40);
        (await test.Db.CharacterConsumableSlots.SingleAsync()).ItemCode = code;
        (await test.Db.CharacterItemStacks.SingleAsync()).ItemCode = code;
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        var services = ProductionPotionServices(test);
        var preview = (await services.Supplies.GetAsync(test.Token, test.Character.Id)).Response!;
        Assert.Equal(percent, preview.Items.Single(item => item.Code == code).HealAmount);
        Assert.Equal(0, services.Catalog.FindItem(code)!.HealAmount);
        Assert.Null((await services.Battle.StartPreparationAsync(test.Room.Id, test.Token)).Error);
        Assert.Equal(20 + percent - 1, test.Character.Hp);
        Assert.Equal(4, (await test.Db.BattleConsumableCooldowns.SingleAsync()).ReadyAtRound);
    }

    [Fact]
    public async Task HealingPotionConditionRechecksHealthAfterProfessionHealing()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 30, characterAttack: 1, monsterAttack: 1);
        test.Character.ProfessionCode = "cleric";
        await test.AddSkillAsync(test.Character, 1, "cleric-heal", true, 70);
        await test.AddPotionAsync(test.Character, 2, true, 40);
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        var services = ProductionPotionServices(test);
        Assert.Null((await services.Battle.StartPreparationAsync(test.Room.Id, test.Token)).Error);
        Assert.Equal(49, test.Character.Hp); // Profession healing crosses the potion's 40% condition.
        Assert.Equal(2, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Empty(await test.Db.BattleHealingPotionStates.ToListAsync());
    }
}
