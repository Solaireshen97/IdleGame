using Game.Server.Data;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public sealed class CombatConsumableMigrationTests
{
    [Fact]
    public async Task CutoverClosesRoomsReleasesBattleAndClearsLoadoutsWithoutSpendingResources()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.GetService<IMigrator>().MigrateAsync("20260927060000_AddParallelPlanting");
        var now = DateTime.UtcNow;
        db.AddRange(new User { Id = 1, UserName = "cutover", PasswordHash = "x" },
            new Character { Id = 1, UserId = 1, Name = "Battle", Hp = 37, MaxHp = 100, Attack = 20, Gold = 123 },
            new Character { Id = 2, UserId = 1, Name = "Production", Hp = 80, MaxHp = 100, Attack = 20 },
            new Room { Id = 7, OwnerUserId = 1, DungeonId = 1, MonsterId = 1, SlotCount = 5,
                Status = RoomStatus.Preparing, IsRepeatBattle = true, PreparationStartedAtUtc = now },
            new CharacterActivity { CharacterId = 1, Kind = "Battle", SourceId = 7, StartedAtUtc = now },
            new CharacterActivity { CharacterId = 2, Kind = "Production", SourceId = 3, StartedAtUtc = now },
            new CharacterItemStack { CharacterId = 1, ItemCode = "minor-healing-potion", Quantity = 17 },
            new CharacterConsumableSlot { CharacterId = 1, SlotIndex = 1, ItemCode = "whetstone-oil", AutoUseEnabled = true },
            new CharacterConsumableSlot { CharacterId = 2, SlotIndex = 3, ItemCode = "northshire-battle-draught" },
            new RoomOperation { RoomId = 7, UserId = 1, CharacterId = 2, CharacterName = "Production",
                SlotIndex = 2, CreatedAtUtc = now },
            new ProductionTask { Id = 3, CharacterId = 2, UserId = 1, RecipeCode = "minor-healing-potion",
                OutputCode = "minor-healing-potion", OutputQuantity = 1, IngredientsJson = "[]", CycleSeconds = 10,
                StartedAtUtc = now, EndsAtUtc = now.AddHours(1), NextCycleAtUtc = now.AddSeconds(10),
                CompletedCycles = 5, TotalQuantity = 5 },
            new RewardRun { RoomId = 7, Sequence = 1, Status = "Settled", SettledAtUtc = now },
            new RewardEntry { RoomId = 7, Sequence = 1, CharacterId = 1, UserId = 1,
                EventKey = "preserved", Kind = "Gold", Quantity = 50 });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO RoomSlots (RoomId, SlotIndex, UserId, CharacterId, IsConfirmed, IsAutoEnabled,
                IsTemporaryAuto, PendingSkillSlotMask, PendingConsumableSlotIndex, IsSoulImprintQueued,
                HasParticipatedInRun)
            VALUES (7, 1, 1, 1, 1, 0, 0, 0, 1, 0, 1);
            """);
        await db.GetService<IMigrator>().MigrateAsync("20260927100000_SpecializeCombatConsumables");
        db.ChangeTracker.Clear();
        var room = await db.Rooms.SingleAsync();
        Assert.NotNull(room.ClosedAtUtc);
        Assert.False(room.IsRepeatBattle);
        Assert.Null(room.PreparationStartedAtUtc);
        Assert.Empty(await db.CharacterActivities.Where(activity => activity.Kind == "Battle").ToListAsync());
        Assert.Equal("Production", (await db.CharacterActivities.SingleAsync()).Kind);
        Assert.Empty(await db.CharacterConsumableSlots.ToListAsync());
        Assert.All(await db.RoomSlots.ToListAsync(), slot =>
        {
            Assert.Null(slot.CharacterId);
            Assert.Null(slot.UserId);
            Assert.Equal(0, slot.PendingConsumableSlotMask);
        });
        Assert.NotEqual("Pending", (await db.RoomOperations.SingleAsync()).Status);
        Assert.Equal(17, (await db.CharacterItemStacks.SingleAsync()).Quantity);
        var character = await db.Characters.FindAsync(1);
        Assert.Equal(37, character!.Hp);
        Assert.Equal(123, character.Gold);
        var production = await db.ProductionTasks.SingleAsync();
        Assert.Equal("Running", production.Status);
        Assert.Equal(5, production.CompletedCycles);
        Assert.Equal(5, production.TotalQuantity);
        Assert.Equal("Settled", (await db.RewardRuns.SingleAsync()).Status);
        Assert.Equal(50, (await db.RewardEntries.SingleAsync()).Quantity);
        Assert.Empty(await db.BattleHealingPotionStates.ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
