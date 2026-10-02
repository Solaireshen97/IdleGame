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
        // Seed the historical schema without columns introduced by later migrations.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Rooms (Id,DungeonId,MonsterId,OwnerUserId,SlotCount,Status,IsPreparationTimeoutEnabled,
                IsRepeatBattle,RoundNumber,RunSequence,Version,CurrentWaveNumber,TotalWaveCount,PreparationStartedAtUtc)
            VALUES (7,1,1,1,5,{(int)RoomStatus.Preparing},1,1,0,1,0,1,1,{now});
            """);
        db.AddRange(new User { Id = 1, UserName = "cutover", PasswordHash = "x" },
            new Character { Id = 1, UserId = 1, Name = "Battle", Hp = 37, MaxHp = 100, Attack = 20, Gold = 123 },
            new Character { Id = 2, UserId = 1, Name = "Production", Hp = 80, MaxHp = 100, Attack = 20 },
            new CharacterActivity { CharacterId = 1, Kind = "Battle", SourceId = 7, StartedAtUtc = now },
            new CharacterActivity { CharacterId = 2, Kind = "Production", SourceId = 3, StartedAtUtc = now },
            new CharacterItemStack { CharacterId = 1, ItemCode = "minor-healing-potion", Quantity = 17 },
            new ProductionTask { Id = 3, CharacterId = 2, UserId = 1, RecipeCode = "minor-healing-potion",
                OutputCode = "minor-healing-potion", OutputQuantity = 1, IngredientsJson = "[]", CycleSeconds = 10,
                StartedAtUtc = now, EndsAtUtc = now.AddHours(1), NextCycleAtUtc = now.AddSeconds(10),
                CompletedCycles = 5, TotalQuantity = 5 },
            new RewardRun { RoomId = 7, Sequence = 1, Status = "Settled", SettledAtUtc = now });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO CharacterConsumableSlots (CharacterId, SlotIndex, ItemCode, AutoUseEnabled, AutoHpThresholdPercent, Version)
            VALUES (1, 1, 'whetstone-oil', 1, 50, 0), (2, 3, 'northshire-battle-draught', 0, 50, 0);
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO RoomOperations(RoomId,UserId,Kind,SlotIndex,CharacterId,CharacterName,Status,CreatedAtUtc,Version)
            VALUES(7,1,0,2,2,'Production','Pending',{now},0);
            """);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO RewardEntries (RoomId,Sequence,CharacterId,UserId,EventKey,Kind,Code,Quantity)
            VALUES (7,1,1,1,'preserved','Gold','',50);
            INSERT INTO RoomSlots (RoomId, SlotIndex, UserId, CharacterId, IsConfirmed, IsAutoEnabled,
                IsTemporaryAuto, PendingSkillSlotMask, PendingConsumableSlotIndex, IsSoulImprintQueued,
                HasParticipatedInRun)
            VALUES (7, 1, 1, 1, 1, 0, 0, 0, 1, 0, 1);
            """);
        await db.GetService<IMigrator>().MigrateAsync("20260927100000_SpecializeCombatConsumables");
        db.ChangeTracker.Clear();
        var room = await db.Rooms.Select(item => new { item.ClosedAtUtc, item.IsRepeatBattle, item.PreparationStartedAtUtc }).SingleAsync();
        Assert.NotNull(room.ClosedAtUtc);
        Assert.False(room.IsRepeatBattle);
        Assert.Null(room.PreparationStartedAtUtc);
        Assert.Empty(await db.CharacterActivities.Where(activity => activity.Kind == "Battle").ToListAsync());
        Assert.Equal("Production", (await db.CharacterActivities.SingleAsync()).Kind);
        Assert.False(await db.CharacterConsumableSlots.AnyAsync());
        Assert.All(await db.RoomSlots.Select(slot => new { slot.CharacterId, slot.UserId, slot.PendingConsumableSlotMask }).ToListAsync(), slot =>
        {
            Assert.Null(slot.CharacterId);
            Assert.Null(slot.UserId);
            Assert.Equal(0, slot.PendingConsumableSlotMask);
        });
        Assert.NotEqual("Pending", await db.RoomOperations.Select(operation => operation.Status).SingleAsync());
        Assert.Equal(17, (await db.CharacterItemStacks.SingleAsync()).Quantity);
        var character = await db.Characters.FindAsync(1);
        Assert.Equal(37, character!.Hp);
        Assert.Equal(123, character.Gold);
        var production = await db.ProductionTasks.SingleAsync();
        Assert.Equal("Running", production.Status);
        Assert.Equal(5, production.CompletedCycles);
        Assert.Equal(5, production.TotalQuantity);
        Assert.Equal("Settled", (await db.RewardRuns.SingleAsync()).Status);
        Assert.Equal(50, await db.RewardEntries.Select(item => item.Quantity).SingleAsync());
        Assert.False(await db.BattleHealingPotionStates.AnyAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
