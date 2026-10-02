using Game.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public sealed class SoulImprintAutoConditionMigrationTests
{
    [Fact]
    public async Task MigrationPreservesExistingSoulAndFormationAndCanBeReapplied()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261002010000_AddBattleFormations");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO Users(Id, UserName, PasswordHash) VALUES(1, 'owner', 'x');
            INSERT INTO Characters(Id, UserId, Name, Hp, MaxHp, Attack) VALUES(1, 1, 'hero', 80, 100, 20);
            INSERT INTO CharacterSoulImprints(Id, CharacterId, SoulImprintCode, EquippedSlotIndex, AutoUseEnabled, IsLocked, AcquiredAtUtc, Version)
            VALUES(1, 1, 'deep-core', 1, 1, 1, '2026-10-01', 7);
            INSERT INTO CharacterBattleFormations(Id, CharacterId, GroupElement, Position, Name, ProfessionCode, SoulImprintId, SoulAutoUseEnabled, Version, IsDeleted, CreatedAtUtc, UpdatedAtUtc)
            VALUES(1, 1, 0, 1, 'test', 'knight', 1, 1, 3, 0, '2026-10-01', '2026-10-01');
            """);
        await db.Database.MigrateAsync();
        var soul = await db.CharacterSoulImprints.AsNoTracking().SingleAsync();
        Assert.Equal((true, true, 7, 70, (string?)null),
            (soul.AutoUseEnabled, soul.IsLocked, soul.Version, soul.AutoHpThresholdPercent, soul.AutoConditionOverride));
        var formation = await db.CharacterBattleFormations.AsNoTracking().SingleAsync();
        Assert.Equal((1, true, 3, 70, (string?)null),
            (formation.SoulImprintId, formation.SoulAutoUseEnabled, formation.Version, formation.SoulAutoHpThresholdPercent, formation.SoulAutoConditionOverride));
        Assert.False(db.Database.HasPendingModelChanges());
        await migrator.MigrateAsync("20261002010000_AddBattleFormations");
        await db.Database.MigrateAsync();
        Assert.Equal(7, (await db.CharacterSoulImprints.AsNoTracking().SingleAsync()).Version);
        Assert.Equal(70, (await db.CharacterBattleFormations.AsNoTracking().SingleAsync()).SoulAutoHpThresholdPercent);
    }
}
