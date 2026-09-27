using Game.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public sealed class MainControlRemovalMigrationTests
{
    [Fact]
    public async Task UpgradePreservesOwnerAutoFromRearSlotAndLeavesGuestPreferencesIndependent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260927000000_AddPartyHpScaling");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO Rooms (Id,DungeonId,MonsterId,OwnerUserId,SlotCount,Status,IsPreparationTimeoutEnabled,
                IsRepeatBattle,RoundNumber,RunSequence,Version,CurrentWaveNumber,TotalWaveCount)
            VALUES (501,1,1,1,5,0,1,0,0,1,0,1,1), (502,1,1,1,5,0,1,0,0,1,0,1,1),
                   (503,1,1,1,5,0,1,0,0,1,0,1,1), (504,1,1,1,5,0,1,0,0,1,0,1,1);
            INSERT INTO RoomSlots (Id,RoomId,SlotIndex,CharacterId,UserId,IsMainControl,IsAutoEnabled,IsConfirmed)
            VALUES (501,501,1,501,1,0,0,0), (502,501,5,502,1,1,1,0), (503,501,3,503,2,0,0,0),
                   (504,502,1,504,1,0,1,0), (505,502,3,505,1,1,0,0), (506,502,5,506,2,0,1,0),
                   (507,503,2,507,1,0,1,0), (508,504,1,508,2,0,1,0);
            """);

        await migrator.MigrateAsync();

        Assert.False(db.Database.HasPendingModelChanges());
        var rooms = await db.Rooms.Where(room => room.Id >= 501).OrderBy(room => room.Id).ToListAsync();
        Assert.Equal(new[] { true, false, true, false }, rooms.Select(room => room.IsOwnerAutoEnabled));
        var slots = await db.RoomSlots.OrderBy(slot => slot.Id).ToListAsync();
        Assert.Equal(new[] { true, true, false, false, false, true, true, true }, slots.Select(slot => slot.IsAutoEnabled));
        Assert.Equal(new[] { 501, 502, 503, 504, 505, 506, 507, 508 }, slots.Select(slot => slot.CharacterId!.Value));
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('RoomSlots') WHERE name = 'IsMainControl'";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }

        // A rollback can reconstruct the legacy switch without losing the new preference.
        rooms[0].IsOwnerAutoEnabled = false;
        rooms[1].IsOwnerAutoEnabled = true;
        await db.SaveChangesAsync();
        await migrator.MigrateAsync("20260927000000_AddPartyHpScaling");
        await using var restored = connection.CreateCommand();
        restored.CommandText = "SELECT RoomId,IsAutoEnabled FROM RoomSlots WHERE IsMainControl = 1 ORDER BY RoomId";
        await using var reader = await restored.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal((501, 0), (reader.GetInt32(0), reader.GetInt32(1)));
        Assert.True(await reader.ReadAsync());
        Assert.Equal((502, 1), (reader.GetInt32(0), reader.GetInt32(1)));
        Assert.True(await reader.ReadAsync());
        Assert.Equal((503, 1), (reader.GetInt32(0), reader.GetInt32(1)));
        Assert.False(await reader.ReadAsync());
    }
}
