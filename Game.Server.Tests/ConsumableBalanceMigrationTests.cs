using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Formations;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public sealed class ConsumableBalanceMigrationTests
{
    [Fact]
    public async Task UpgradeMergesBottlesReferencesAndPendingProductionWithoutResettingBattle()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261002030000_AddBattleStatistics");
        var loadout = new CombatLoadoutDefinition
        {
            Consumables = [new FormationConsumableChoice { SlotIndex = 1, ItemCode = "travel-healing-potion",
                AutoUseEnabled = true, AutoHpThresholdPercent = 37 }]
        };
        var json = CombatLoadoutCodec.Serialize(loadout);
        db.AddRange(new User { Id = 1, UserName = "migration", PasswordHash = "unused" },
            new Character { Id = 1, UserId = 1, Name = "hero", Hp = 37, MaxHp = 100, Gold = 123 },
            new Character { Id = 2, UserId = 1, Name = "guest", Hp = 90, MaxHp = 100 },
            new Room { Id = 7, DungeonId = 1, MonsterId = 1, OwnerUserId = 1, RoundNumber = 15, RunSequence = 3 },
            new RoomSlot { RoomId = 7, SlotIndex = 1, UserId = 1, CharacterId = 1, AppliedLoadoutJson = json },
            new RoomOperation { RoomId = 7, UserId = 1, CharacterId = 2, SlotIndex = 2,
                RequestedLoadoutJson = json },
            new CharacterBattleFormation { Id = 1, CharacterId = 1, Name = "saved", Position = 1 },
            new CharacterFormationState { CharacterId = 1, AppliedFormationId = 1, AppliedChoiceHash = "old-hash" },
            new CharacterItemStack { CharacterId = 1, ItemCode = "minor-healing-potion", Quantity = 7 },
            new CharacterItemStack { CharacterId = 1, ItemCode = "travel-healing-potion", Quantity = 11 },
            new CharacterItemStack { CharacterId = 1, ItemCode = "lesser-travel-healing-potion", Quantity = 13 },
            new CharacterItemStack { CharacterId = 1, ItemCode = "peacebloom", Quantity = 29 },
            new ProductionTask { CharacterId = 1, UserId = 1, RecipeCode = "travel-healing-potion-elwynn",
                OutputCode = "travel-healing-potion", OutputQuantity = 2, IngredientsJson = "[]",
                CompletedCycles = 5, TotalQuantity = 10, CycleSeconds = 10 },
            new ProductionTask { CharacterId = 2, UserId = 1, RecipeCode = "elwynn-assault-legacy-batch",
                OutputCode = "elwynn-assault-draught", OutputQuantity = 3, IngredientsJson = "[]",
                CompletedCycles = 2, TotalQuantity = 6, CycleSeconds = 10 },
            new RewardEntry { RoomId = 7, Sequence = 3, CharacterId = 1, UserId = 1, EventKey = "pending",
                Kind = "Consumable", Code = "travel-healing-potion", Quantity = 4 });
        for (var round = 0; round < 3; round++)
            db.BattleConsumableBuffs.Add(new BattleConsumableBuff { RoomId = 7, RunSequence = 3, CharacterId = 1,
                ItemCode = "whetstone-oil", WeaponSkillCode = "weapon-attack", SkillLevel = 3,
                AppliedRound = round * 6, ExpiresAfterRound = round * 6 + 2 });
        db.BattleConsumableBuffs.Add(new BattleConsumableBuff { RoomId = 7, RunSequence = 3, CharacterId = 2,
            ItemCode = "whetstone-oil", WeaponSkillCode = "weapon-attack", SkillLevel = 3, AppliedRound = 0, ExpiresAfterRound = 2 });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO CharacterConsumableSlots (CharacterId, SlotIndex, ItemCode, AutoUseEnabled, AutoHpThresholdPercent, Version)
                VALUES (1, 1, 'travel-healing-potion', 1, 37, 0);
            INSERT INTO FormationConsumableSlots (FormationId, SlotIndex, ItemCode, AutoUseEnabled, AutoHpThresholdPercent)
                VALUES (1, 1, 'travel-healing-potion', 1, 37);
            INSERT INTO BattleHealingPotionStates (RoomId, RunSequence, CharacterId, UsesUsed, Version)
                VALUES (7, 3, 1, 1, 0);
            """);
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(18, (await db.CharacterItemStacks.SingleAsync(stack => stack.ItemCode == "minor-healing-potion")).Quantity);
        Assert.Equal(13, (await db.CharacterItemStacks.SingleAsync(stack => stack.ItemCode == "lesser-minor-healing-potion")).Quantity);
        Assert.Equal(29, (await db.CharacterItemStacks.SingleAsync(stack => stack.ItemCode == "peacebloom")).Quantity);
        Assert.False(await db.CharacterItemStacks.AnyAsync(stack => stack.ItemCode.Contains("travel-healing")));
        var potion = await db.CharacterConsumableSlots.SingleAsync();
        Assert.Equal(("minor-healing-potion", true, 37), (potion.ItemCode, potion.AutoUseEnabled, potion.AutoHpThresholdPercent));
        Assert.Equal("minor-healing-potion", (await db.FormationConsumableSlots.SingleAsync()).ItemCode);
        var slot = await db.RoomSlots.SingleAsync();
        Assert.Equal("minor-healing-potion", CombatLoadoutCodec.Deserialize(slot.AppliedLoadoutJson!).Consumables.Single().ItemCode);
        Assert.Equal("minor-healing-potion", CombatLoadoutCodec.Deserialize((await db.RoomOperations.SingleAsync()).RequestedLoadoutJson!).Consumables.Single().ItemCode);
        var task = await db.ProductionTasks.SingleAsync(task => task.CharacterId == 1);
        Assert.Equal(("minor-healing-potion", 2, 5, 10, 6), (task.OutputCode, task.OutputQuantity, task.CompletedCycles, task.TotalQuantity, task.TargetCycles));
        Assert.Equal(3, (await db.ProductionTasks.SingleAsync(task => task.CharacterId == 2)).TargetCycles);
        Assert.Equal(("minor-healing-potion", 4), ((await db.RewardEntries.SingleAsync()).Code, (await db.RewardEntries.SingleAsync()).Quantity));
        var uses = await db.BattleHealingPotionStates.SingleAsync(state => state.CharacterId == 1);
        Assert.Equal((1, 2), (uses.UsesUsed, uses.BuffUsesUsed));
        Assert.Equal((0, 1), ((await db.BattleHealingPotionStates.SingleAsync(state => state.CharacterId == 2)).UsesUsed,
            (await db.BattleHealingPotionStates.SingleAsync(state => state.CharacterId == 2)).BuffUsesUsed));
        Assert.Null((await db.Rooms.SingleAsync()).ClosedAtUtc);
        Assert.Equal((15, 3), ((await db.Rooms.SingleAsync()).RoundNumber, (await db.Rooms.SingleAsync()).RunSequence));
        Assert.Equal((37, 123), ((await db.Characters.FindAsync(1))!.Hp, (await db.Characters.FindAsync(1))!.Gold));
        Assert.Null((await db.CharacterFormationStates.SingleAsync()).AppliedChoiceHash);
        // Running the upgrade twice must neither merge twice nor replenish allowances.
        await db.Database.MigrateAsync();
        Assert.Equal(18, (await db.CharacterItemStacks.SingleAsync(stack => stack.ItemCode == "minor-healing-potion")).Quantity);
    }
}
