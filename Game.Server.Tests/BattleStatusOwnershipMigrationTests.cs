using Game.Server.Data;
using Game.Shared.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public sealed class BattleStatusOwnershipMigrationTests
{
    [Fact]
    public async Task ExistingBoundResourcesUpgradeWithoutChangingRealPeriodicSnapshots()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.GetService<IMigrator>().MigrateAsync("20260929010000_CharacterFirstHuntWeaponClaims");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO BattleStatusEffects
                (RoomId, RunSequence, TargetType, TargetId, EffectCode, Stacks, AppliedRound, ExpiresAfterRound, PerTickValue)
            VALUES
                (7, 1, 'Character', 11, 'mage-disorder', 2, 0, 2147483647, 51),
                (7, 1, 'Character', 12, 'hunter-prey-mark', 1, 0, 3, 51),
                (7, 1, 'Monster', 51, 'mage-scorch-dot', 1, 0, 3, 23),
                (7, 1, 'Character', 11, 'acolyte-revelation', 2, 0, 2147483647, NULL);
            """);
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        var effects = await db.BattleStatusEffects.OrderBy(effect => effect.Id).ToListAsync();
        Assert.All(effects.Take(2), effect =>
        {
            Assert.Equal("Monster", effect.BoundTargetType);
            Assert.Equal(51, effect.BoundTargetId);
            Assert.Equal("Character", effect.SourceActorType);
            Assert.Equal(effect.TargetId, effect.SourceActorId);
            Assert.Null(effect.PerTickValue);
        });
        Assert.Equal(BattleStatusLifetime.Encounter, effects[0].Lifetime);
        Assert.Equal(BattleStatusLifetime.Rounds, effects[1].Lifetime);
        Assert.Equal(23, effects[2].PerTickValue);
        Assert.Null(effects[2].BoundTargetId);
        Assert.Equal(BattleStatusLifetime.UntilConsumed, effects[3].Lifetime);
        Assert.Equal(2, effects[3].Stacks);
        await db.GetService<IMigrator>().MigrateAsync("20260929010000_CharacterFirstHuntWeaponClaims");
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PerTickValue FROM BattleStatusEffects ORDER BY Id";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(51, reader.GetInt32(0));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(51, reader.GetInt32(0));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(23, reader.GetInt32(0));
    }
}
