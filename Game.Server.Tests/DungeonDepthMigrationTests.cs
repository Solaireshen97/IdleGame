using System.Text.Json;
using Game.Server.Data;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public sealed class DungeonDepthMigrationTests
{
    [Fact]
    public async Task UpgradeBackfillsOnlyProvenCharacterClearsAndPreservesRunningRoomAndSettledRewards()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-depth-migration-{Guid.NewGuid():N}.db");
        try
        {
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20260927100000_SpecializeCombatConsumables");
            var now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
            object[] legacyEntities =
            [
                new User { Id = 1, UserName = "legacy", PasswordHash = "x", ActiveCharacterId = 1 },
                new Character { Id = 1, UserId = 1, Name = "Cleared", Hp = 37, MaxHp = 123, Attack = 29, Gold = 765, Experience = 12 },
                new Character { Id = 2, UserId = 1, Name = "Uncleared Alt", Hp = 80, MaxHp = 100, Attack = 20, Gold = 17 },
                new Dungeon { Id = 9001, Code = "ember-rift", Name = "Legacy Dungeon", DungeonKind = "Elite", MonsterName = "Boss",
                    MonsterMaxHp = 2000, MonsterAttack = 40, MonsterDefense = 8, SlotCount = 5 },
                new UserDungeonClear { UserId = 1, DungeonId = 9001, ClearedAtUtc = now },
                new CharacterBattleMilestone { CharacterId = 1, Kind = "DungeonClear", TargetCode = "ember-rift", Count = 3, FirstAtUtc = now, LastAtUtc = now },
                new CharacterBattleMilestone { CharacterId = 2, Kind = "MonsterKill", TargetCode = "ember-rift", Count = 1, FirstAtUtc = now, LastAtUtc = now },
                new Room { Id = 7, OwnerUserId = 1, DungeonId = 9001, MonsterId = 9011, SlotCount = 5, Status = RoomStatus.Preparing,
                    RoundNumber = 6, RunSequence = 3, CurrentWaveNumber = 2, TotalWaveCount = 3, ScalingPartySize = 2,
                    IsRepeatBattle = true, IsOwnerAutoEnabled = true, PreparationStartedAtUtc = now, StartedAtUtc = now.AddMinutes(-2), Version = 9 },
                new Monster { Id = 9011, RoomId = 7, WaveNumber = 2, Position = 1, Name = "Wounded boss", IsBoss = true,
                    Hp = 417, BaseMaxHp = 2000, MaxHp = 3200, Attack = 57, Defense = 13, CombatProfileCode = "legacy-boss", RewardProfileCode = "legacy-loot" },
                new Monster { Id = 9012, RoomId = 7, WaveNumber = 3, Position = 1, Name = "Future wave", Hp = 850, BaseMaxHp = 500, MaxHp = 850, Attack = 26, Defense = 4 },
                new RoomSlot { Id = 1, RoomId = 7, SlotIndex = 1, UserId = 1, CharacterId = 1, IsAutoEnabled = true, IsConfirmed = true, HasParticipatedInRun = true },
                new RoomSlot { Id = 2, RoomId = 7, SlotIndex = 3, UserId = 1, CharacterId = 2, IsAutoEnabled = false, IsConfirmed = true, HasParticipatedInRun = true },
                new CharacterItemStack { Id = 1, CharacterId = 1, ItemCode = "minor-healing-potion", Quantity = 17, Version = 4 },
                new CharacterItemStack { Id = 2, CharacterId = 2, ItemCode = "weapon-fragment-t1", Quantity = 93, Version = 2 },
                new RewardRun { RoomId = 7, Sequence = 2, Status = "Settled", SettledAtUtc = now.AddMinutes(-3) },
                new RewardEvent { RoomId = 7, Sequence = 2, EventKey = "clear" },
                new RewardEntry { Id = 1, RoomId = 7, Sequence = 2, EventKey = "clear", UserId = 1, CharacterId = 1, Kind = "Gold", Quantity = 50 },
                new RewardEntry { Id = 2, RoomId = 7, Sequence = 2, EventKey = "clear", UserId = 1, CharacterId = 1, Kind = "Consumable", Code = "minor-healing-potion", Quantity = 2 }
            ];
            foreach (var entity in legacyEntities) await InsertIntoHistoricalSchemaAsync(db, connection, entity);

            string[] preservedTables = ["Users", "Characters", "Dungeons", "UserDungeonClears", "CharacterBattleMilestones",
                "Rooms", "Monsters", "RoomSlots", "CharacterItemStacks", "RewardRuns", "RewardEvents", "RewardEntries"];
            var snapshots = new Dictionary<string, (List<string> Columns, string Rows)>();
            foreach (var table in preservedTables)
            {
                var columns = await ColumnsAsync(connection, table);
                snapshots.Add(table, (columns, await RowsAsync(connection, table, columns)));
            }

            await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Contains("20260928010000_AddDungeonDepthMastery", await db.Database.GetAppliedMigrationsAsync());
            var progress = Assert.Single(await db.CharacterDungeonProgress.ToListAsync());
            Assert.Equal((1, 9001, 1, 0), (progress.CharacterId, progress.DungeonId, progress.HighestDepth, progress.Version));
            Assert.False(await db.CharacterDungeonProgress.AnyAsync(item => item.CharacterId == 2));
            var account = await db.UserDungeonClears.SingleAsync();
            Assert.Equal((1, 0), (account.HighestDepth, account.Version));
            var room = await db.Rooms.SingleAsync();
            Assert.Equal(1, room.DepthLevel);
            Assert.Null(room.DepthDefinitionJson);
            Assert.Equal((6, 2, 3, 2, 9), (room.RoundNumber, room.CurrentWaveNumber, room.TotalWaveCount, room.ScalingPartySize, room.Version));
            Assert.Equal(RoomStatus.Preparing, room.Status);
            Assert.Empty(await db.DungeonRunParticipants.ToListAsync());
            Assert.All(await db.RewardEntries.ToListAsync(), entry => Assert.Equal("Base", entry.RewardSource));

            foreach (var (table, snapshot) in snapshots)
                Assert.Equal(snapshot.Rows, await RowsAsync(connection, table, snapshot.Columns));

            // Repeating migration and retrying an already settled run must not award it again.
            await db.Database.MigrateAsync();
            var settledAttempt = new Room { Id = room.Id, DungeonId = room.DungeonId, RunSequence = 2 };
            var logs = new List<string>();
            var rewards = RewardTestFactory.CreateService(db, ProgressionTestFactory.Create());
            await rewards.SettleAsync(settledAttempt, true, now.AddDays(1), logs);
            await db.SaveChangesAsync();
            Assert.Empty(logs);
            Assert.Single(await db.CharacterDungeonProgress.ToListAsync());
            Assert.Equal("Settled", (await db.RewardRuns.SingleAsync()).Status);
            foreach (var (table, snapshot) in snapshots)
                Assert.Equal(snapshot.Rows, await RowsAsync(connection, table, snapshot.Columns));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // Current entities are fixtures only: write solely the columns present at the migration under test.
    private static async Task InsertIntoHistoricalSchemaAsync(GameDbContext db, SqliteConnection connection, object entity)
    {
        var metadata = db.Model.FindEntityType(entity.GetType())!;
        var table = metadata.GetTableName()!;
        var existing = await ColumnsAsync(connection, table);
        var identifier = StoreObjectIdentifier.Table(table, metadata.GetSchema());
        var properties = metadata.GetProperties().Where(property => existing.Contains(property.GetColumnName(identifier)!)).ToList();
        await using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO \"{table}\" ({string.Join(",", properties.Select(property => $"\"{property.GetColumnName(identifier)}\""))}) " +
            $"VALUES ({string.Join(",", properties.Select((_, index) => $"$p{index}"))})";
        for (var index = 0; index < properties.Count; index++)
        {
            var property = properties[index];
            var value = property.PropertyInfo!.GetValue(entity);
            var converter = property.GetTypeMapping().Converter;
            if (converter is not null) value = converter.ConvertToProvider(value);
            command.Parameters.AddWithValue($"$p{index}", value ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> ColumnsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        return columns;
    }

    private static async Task<string> RowsAsync(SqliteConnection connection, string table, List<string> columns)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(",", columns.Select(column => $"\"{column}\""))} FROM \"{table}\" ORDER BY rowid";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(values.Select(value => value is DBNull ? null : value).ToArray());
        }
        return JsonSerializer.Serialize(rows);
    }
}
