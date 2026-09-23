using Game.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public sealed class CharacterActivityMigrationTests
{
    [Fact]
    public async Task ExistingRepeatRoomGetsDeadlineAndActivityReservation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-activity-migration-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using (var db = new GameDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20260923000000_AddCharacterSlots");
                await db.Database.ExecuteSqlRawAsync("INSERT INTO Users (Id, UserName, PasswordHash) VALUES (1, 'owner', 'hash')");
                await db.Database.ExecuteSqlRawAsync("INSERT INTO Characters (Id, UserId, Name, Hp, MaxHp, Attack) VALUES (1, 1, 'Fighter', 100, 100, 20)");
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO Rooms (Id, DungeonId, MonsterId, OwnerUserId, SlotCount, Status,
                        IsPreparationTimeoutEnabled, IsRepeatBattle, RoundNumber, RunSequence,
                        CurrentWaveNumber, TotalWaveCount, Version)
                    VALUES (7, 1, 1, 1, 5, 0, 1, 1, 0, 1, 1, 1, 0);
                    INSERT INTO RoomSlots (RoomId, SlotIndex, CharacterId, UserId, IsMainControl,
                        IsConfirmed, IsAutoEnabled, IsTemporaryAuto, PendingSkillSlotMask)
                    VALUES (7, 1, 1, 1, 1, 0, 0, 0, 0);
                    """);
            }

            await using (var db = new GameDbContext(options))
            {
                await db.Database.MigrateAsync();
                var room = await db.Rooms.SingleAsync();
                var activity = await db.CharacterActivities.SingleAsync();
                Assert.InRange(room.ExpiresAtUtc!.Value - room.StartedAtUtc!.Value,
                    TimeSpan.FromHours(12).Subtract(TimeSpan.FromSeconds(1)),
                    TimeSpan.FromHours(12).Add(TimeSpan.FromSeconds(1)));
                Assert.Equal((1, "Battle", 7), (activity.CharacterId, activity.Kind, activity.SourceId));
                Assert.Equal(room.ExpiresAtUtc, activity.EndsAtUtc);
                Assert.False(db.Database.HasPendingModelChanges());
            }
        }
        finally { File.Delete(path); }
    }
}
