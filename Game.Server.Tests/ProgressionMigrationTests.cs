using Game.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public class ProgressionMigrationTests
{
    [Fact]
    public async Task RemovingDefenseTalentRefundsEverySpentPoint()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-defense-removal-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            await using var db = new GameDbContext(options);
            await db.Database.GetService<IMigrator>().MigrateAsync("20260922030000_AddWeaponEconomyTracking");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO Users (Id, UserName, PasswordHash, ActiveCharacterId) VALUES (1, 'owner', 'x', NULL);
                INSERT INTO Characters (Id, UserId, Name, ProfessionCode, Hp, MaxHp, Attack, Defense,
                    Level, Experience, TalentPoints, AttackTalentRank, DefenseTalentRank, HealthTalentRank, Version)
                VALUES (1, 1, 'Knight', 'knight', 100, 100, 20, 5, 4, 0, 2, 0, 2, 0, 0);
                """);

            await db.Database.MigrateAsync();

            var character = await db.Characters.SingleAsync();
            Assert.Equal(3, character.TalentPoints); // Formal tree migration normalizes every character to Level - 1 points.
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ExistingCharacterKeepsCombatStatsAndStartsAtLevelOne()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-progression-migration-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            await using var db = new GameDbContext(options);
            await db.Database.GetService<IMigrator>().MigrateAsync("20260920020000_AddRoundCooldownDuration");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Characters (Id, UserId, Name, Hp, MaxHp, Attack, Defense) VALUES (1, 1, 'Knight', 76, 100, 20, 5)");

            await db.Database.MigrateAsync();
            var character = await db.Characters.SingleAsync();

            Assert.Equal((1, 0, 0), (character.Level, character.Experience, character.TalentPoints));
            Assert.Equal((76, 100, 20), (character.Hp, character.MaxHp, character.Attack));
            Assert.Equal((0, 0, 0), (character.AttackTalentRank, character.HealthTalentRank, character.Version));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
