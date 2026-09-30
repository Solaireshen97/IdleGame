using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Dtos.Characters;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class BattleServiceTests
{
    private static QueueConsumableRequest PotionRequest(BattleTestContext test, int slot = 1, bool queued = true) => new()
    {
        RoomId = test.Room.Id, CharacterId = test.Character.Id, ConsumableSlotIndex = slot, IsQueued = queued,
        ExpectedRoundNumber = test.Room.RoundNumber, ExpectedRunSequence = test.Room.RunSequence
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealingPotionLimitCountsSuccessesAndSurvivesCooldownStockAndSameRunRejoin(bool automatic)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 20, characterAttack: 1, monsterAttack: 1);
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.AddPotionAsync(test.Character, 5, automatic, 100);
        var run = test.Room.RunSequence;
        for (var use = 1; use <= 2; use++)
        {
            test.Character.Hp = 20;
            test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
            foreach (var cooldown in await test.Db.BattleConsumableCooldowns.ToListAsync()) cooldown.ReadyAtRound = 0;
            await test.Db.SaveChangesAsync();
            if (!automatic) Assert.True((await test.Service.QueueConsumableAsync(PotionRequest(test), test.Token)).Success);
            var result = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);
            Assert.Null(result.Error);
            Assert.Contains(result.Result!.Logs, log => log.Contains("使用 小型治疗药水"));
            Assert.Equal(use, (await test.Db.BattleHealingPotionStates.SingleAsync()).UsesUsed);
            Assert.Equal(5 - use, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
        }

        // Replacing a room membership and replenishing inventory must not replenish the run allowance.
        test.Db.RoomSlots.Remove(await test.Db.RoomSlots.SingleAsync());
        await test.Db.SaveChangesAsync();
        test.Db.RoomSlots.Add(new RoomSlot { RoomId = test.Room.Id, SlotIndex = 1,
            UserId = 1, CharacterId = test.Character.Id });
        (await test.Db.CharacterItemStacks.SingleAsync()).Quantity = 9;
        foreach (var cooldown in await test.Db.BattleConsumableCooldowns.ToListAsync()) cooldown.ReadyAtRound = 0;
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        test.Room.CurrentWaveNumber++;
        test.Character.Hp = 20;
        await test.Db.SaveChangesAsync();
        var blocked = await test.Service.QueueConsumableAsync(PotionRequest(test), test.Token);
        Assert.False(blocked.Success);
        Assert.Equal("HealingPotionLimitReached", blocked.Error);
        var third = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(third.Error);
        Assert.DoesNotContain(third.Result!.Logs, log => log.Contains("使用 小型治疗药水"));
        Assert.Equal(9, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Equal(run, test.Room.RunSequence);
        var detail = (await test.GetRoomDetailAsync())!.Slots.Single();
        Assert.Equal(2, detail.HealingPotionUsesUsed);
        Assert.Equal(0, detail.HealingPotionUsesRemaining);
        Assert.Equal(2, detail.HealingPotionUsesLimit);
        Assert.Equal("HealingPotionLimitReached", detail.Consumables.Single(slot => slot.SlotIndex == 1).UnavailableReason);

        test.Room.RunSequence++;
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        Assert.True((await test.Service.QueueConsumableAsync(PotionRequest(test), test.Token)).Success);
        var newRun = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(newRun.Error);
        Assert.Equal(8, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Equal(1, (await test.Db.BattleHealingPotionStates.SingleAsync(state => state.RunSequence == test.Room.RunSequence)).UsesUsed);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(1, 1)]
    [InlineData(0, 2)]
    public async Task ConsumableQueueRejectsMissingAndStaleRoundOrRun(int? round, int? run)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 20);
        await test.AddPotionAsync(test.Character, 3, false);
        var request = PotionRequest(test);
        request.ExpectedRoundNumber = round;
        request.ExpectedRunSequence = run;
        var result = await test.Service.QueueConsumableAsync(request, test.Token);
        Assert.False(result.Success);
        Assert.Equal("StaleRound", result.Error);
        Assert.Equal(0, (await test.Db.RoomSlots.SingleAsync()).PendingConsumableSlotMask);
        Assert.Equal(3, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Empty(await test.Db.BattleHealingPotionStates.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealingAndCombatBuffCanBothBeUsedInTheSameRound(bool automatic)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 20, characterAttack: 1, monsterAttack: 1);
        await test.AddPotionAsync(test.Character, 2, automatic, 100);
        var service = await AddCombatBuffAsync(test, automatic);
        // Queued use and automatic use of the same slot must coalesce into one success.
        Assert.True((await service.QueueConsumableAsync(PotionRequest(test, 1), test.Token)).Success);
        Assert.True((await service.QueueConsumableAsync(PotionRequest(test, 2), test.Token)).Success);
        Assert.Equal(3, (await test.Db.RoomSlots.SingleAsync()).PendingConsumableSlotMask);
        var result = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(result.Error);
        Assert.Contains(result.Result!.Logs, log => log.Contains("使用 小型治疗药水"));
        Assert.All(await test.Db.CharacterItemStacks.ToListAsync(), stack => Assert.Equal(1, stack.Quantity));
        Assert.Single(await test.Db.BattleConsumableBuffs.ToListAsync());
        Assert.Equal(1, (await test.Db.BattleHealingPotionStates.SingleAsync()).UsesUsed);
        Assert.Equal(0, (await test.Db.RoomSlots.SingleAsync()).PendingConsumableSlotMask);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CancellingOneConsumableQueuePreservesTheOtherAndConsumesOnlyThatItem(int cancelledSlot)
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 20, characterAttack: 1, monsterAttack: 1);
        await test.AddPotionAsync(test.Character, 2, false);
        var service = await AddCombatBuffAsync(test, false);
        Assert.True((await service.QueueConsumableAsync(PotionRequest(test, 1), test.Token)).Success);
        Assert.True((await service.QueueConsumableAsync(PotionRequest(test, 2), test.Token)).Success);
        Assert.True((await service.QueueConsumableAsync(PotionRequest(test, cancelledSlot, false), test.Token)).Success);
        Assert.Equal(cancelledSlot == 1 ? 2 : 1, (await test.Db.RoomSlots.SingleAsync()).PendingConsumableSlotMask);
        var result = await service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(result.Error);
        Assert.Equal(cancelledSlot == 1 ? 2 : 1, (await test.Db.CharacterItemStacks.SingleAsync(stack => stack.ItemCode == "minor-healing-potion")).Quantity);
        Assert.Equal(cancelledSlot == 2 ? 2 : 1, (await test.Db.CharacterItemStacks.SingleAsync(stack => stack.ItemCode == "whetstone-oil")).Quantity);
        Assert.Equal(cancelledSlot == 1 ? 0 : 1, await test.Db.BattleHealingPotionStates.SumAsync(state => state.UsesUsed));
    }

    [Fact]
    public async Task ConcurrentSettlementUsesOneInventoryItemAndOneHealingAllowance()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 20, characterAttack: 1, monsterAttack: 1);
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.AddPotionAsync(test.Character, 5, true, 100);
        await using var firstDb = test.CreateDbContext();
        await using var staleDb = test.CreateDbContext();
        await firstDb.Rooms.SingleAsync();
        await staleDb.Rooms.SingleAsync();
        BattleService Service(GameDbContext db)
        {
            var progression = ProgressionTestFactory.Create();
            return new BattleService(db, new UserService(db, progression, SkillTestFactory.Create()),
                ConsumableTestFactory.Create(), SkillTestFactory.Create(), RewardTestFactory.CreateService(db, progression));
        }
        var results = await Task.WhenAll(Service(firstDb).StartPreparationAsync(test.Room.Id, test.Token),
            Service(staleDb).StartPreparationAsync(test.Room.Id, test.Token));
        Assert.Single(results, result => result.Error is null);
        await using var verification = test.CreateDbContext();
        Assert.Equal(4, (await verification.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Equal(1, (await verification.BattleHealingPotionStates.SingleAsync()).UsesUsed);
        Assert.Equal(1, (await verification.Rooms.SingleAsync()).RoundNumber);
    }

    [Fact]
    public async Task FullHealthAutomaticPotionDoesNotSpendStockOrRunAllowance()
    {
        await using var test = await BattleTestContext.CreateAsync(characterAttack: 1, monsterAttack: 1);
        await test.AddPotionAsync(test.Character, 3, true, 100);
        var result = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(result.Error);
        Assert.DoesNotContain(result.Result!.Logs, log => log.Contains("使用 小型治疗药水"));
        Assert.Equal(3, (await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Empty(await test.Db.BattleHealingPotionStates.ToListAsync());
        Assert.Empty(await test.Db.BattleConsumableCooldowns.ToListAsync());
    }

    [Fact]
    public async Task LegalResetStartsNewHealingAllowanceAndRetainsEndedRunUsage()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 20, characterAttack: 1, monsterAttack: 1);
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        await test.AddPotionAsync(test.Character, 5, true, 100);
        var oldRun = test.Room.RunSequence;
        for (var i = 0; i < 2; i++)
        {
            test.Character.Hp = 20;
            test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
            foreach (var cooldown in await test.Db.BattleConsumableCooldowns.ToListAsync()) cooldown.ReadyAtRound = 0;
            await test.Db.SaveChangesAsync();
            Assert.Null((await test.Service.StartPreparationAsync(test.Room.Id, test.Token)).Error);
        }
        test.Monster.Hp = 1;
        test.Room.NextRoundAvailableAtUtc = DateTime.UtcNow.AddSeconds(-1);
        await test.Db.SaveChangesAsync();
        var victory = await test.Service.StartPreparationAsync(test.Room.Id, test.Token);
        Assert.Null(victory.Error);
        Assert.Equal(RoomStatus.BattleOver, test.Room.Status);
        Assert.Equal(2, (await test.GetRoomDetailAsync())!.Slots.Single().HealingPotionUsesUsed);
        Assert.True((await test.Service.ResetBattleAsync(test.Room.Id, test.Token)).Success);
        Assert.True(test.Room.RunSequence > oldRun);
        Assert.Equal(2, (await test.Db.BattleHealingPotionStates.SingleAsync(state => state.RunSequence == oldRun)).UsesUsed);
        Assert.Equal(2, (await test.GetRoomDetailAsync())!.Slots.Single().HealingPotionUsesRemaining);
        test.Character.Hp = 20;
        test.Monster.Hp = test.Monster.MaxHp = 1000;
        await test.Db.SaveChangesAsync();
        Assert.Null((await test.Service.StartPreparationAsync(test.Room.Id, test.Token)).Error);
        Assert.Equal(1, (await test.Db.BattleHealingPotionStates.SingleAsync(state => state.RunSequence == test.Room.RunSequence)).UsesUsed);
    }

    [Fact]
    public async Task ExhaustedHealingAllowanceDoesNotBlockAutomaticCombatBuff()
    {
        await using var test = await BattleTestContext.CreateAsync(characterHp: 20, characterAttack: 1, monsterAttack: 1);
        await test.AddPotionAsync(test.Character, 2, true, 100);
        var service = await AddCombatBuffAsync(test, true);
        test.Db.BattleHealingPotionStates.Add(new BattleHealingPotionState { RoomId = test.Room.Id,
            RunSequence = test.Room.RunSequence, CharacterId = test.Character.Id, UsesUsed = 2 });
        await test.Db.SaveChangesAsync();
        Assert.Null((await service.StartPreparationAsync(test.Room.Id, test.Token)).Error);
        Assert.Equal(2, (await test.Db.CharacterItemStacks.SingleAsync(stack => stack.ItemCode == "minor-healing-potion")).Quantity);
        Assert.Equal(1, (await test.Db.CharacterItemStacks.SingleAsync(stack => stack.ItemCode == "whetstone-oil")).Quantity);
        Assert.Equal(2, (await test.Db.BattleHealingPotionStates.SingleAsync()).UsesUsed);
        Assert.Single(await test.Db.BattleConsumableBuffs.ToListAsync());
    }

    [Fact]
    public async Task LoadoutServiceRejectsHealingInBuffSlotAndBuffInHealingSlot()
    {
        await using var test = await BattleTestContext.CreateAsync();
        var configuration = new ConfigurationBuilder().AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        var weapons = new WeaponCatalog(Options.Create(configuration.GetSection(WeaponOptions.SectionName).Get<WeaponOptions>()!));
        var catalog = new ConsumableCatalog(Options.Create(configuration.GetSection(ConsumableOptions.SectionName).Get<ConsumableOptions>()!), weapons);
        var service = new ConsumableService(test.Db,
            new UserService(test.Db, ProgressionTestFactory.Create(), SkillTestFactory.Create()), catalog);
        Assert.Equal("WrongConsumableSlot", (await service.SetSlotAsync(test.Token, test.Character.Id, 2,
            new SetConsumableSlotRequest { ItemCode = "minor-healing-potion" })).Error);
        Assert.Equal("WrongConsumableSlot", (await service.SetSlotAsync(test.Token, test.Character.Id, 1,
            new SetConsumableSlotRequest { ItemCode = "whetstone-oil" })).Error);
        Assert.Empty(await test.Db.CharacterConsumableSlots.ToListAsync());
    }

    private static async Task<BattleService> AddCombatBuffAsync(BattleTestContext test, bool automatic)
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        var weapons = new WeaponCatalog(Options.Create(configuration.GetSection(WeaponOptions.SectionName).Get<WeaponOptions>()!));
        // Preserve the simple fixture's healing amount while using the production weapon buff curve.
        var items = configuration.GetSection(ConsumableOptions.SectionName).Get<ConsumableOptions>()!;
        items.Items.Single(item => item.Code == "minor-healing-potion").HealAmount = 20;
        var catalog = new ConsumableCatalog(Options.Create(items), weapons);
        test.Db.CharacterItemStacks.Add(new CharacterItemStack { CharacterId = test.Character.Id, ItemCode = "whetstone-oil", Quantity = 2 });
        test.Db.CharacterConsumableSlots.Add(new CharacterConsumableSlot { CharacterId = test.Character.Id,
            SlotIndex = 2, ItemCode = "whetstone-oil", AutoUseEnabled = automatic });
        test.Monster.Hp = test.Monster.MaxHp = 10000;
        await test.Db.SaveChangesAsync();
        var progression = ProgressionTestFactory.Create();
        return new BattleService(test.Db, new UserService(test.Db, progression, SkillTestFactory.Create()), catalog,
            SkillTestFactory.Create(), RewardTestFactory.CreateService(test.Db, progression), weaponCatalog: weapons);
    }
}
