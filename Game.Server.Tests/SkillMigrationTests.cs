using Game.Server.Data;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public class SkillMigrationTests
{
    [Fact]
    public async Task UnifiedTreeMigrationRefundsOldSkillNodesAndUnequipsTheirSkills()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-unified-talents-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            await using var db = new GameDbContext(options);
            await db.Database.GetService<IMigrator>().MigrateAsync("20260921000000_AddSkillTalentTree");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO Users (Id, UserName, PasswordHash, ActiveCharacterId) VALUES (1, 'owner', 'x', 1);
                INSERT INTO Characters (Id, UserId, Name, ProfessionCode, Level, Experience, TalentPoints,
                    AttackTalentRank, DefenseTalentRank, HealthTalentRank, Hp, MaxHp, Attack, Defense, Version)
                VALUES (1, 1, 'Knight', 'knight', 3, 0, 0, 1, 0, 0, 100, 100, 20, 5, 0);
                INSERT INTO CharacterSkillTalents (CharacterId, NodeCode, PointsSpent)
                VALUES (1, 'knight-vanguard', 1);
                INSERT INTO CharacterSkillSlots (CharacterId, SlotIndex, SkillCode, AutoUseEnabled, AutoHpThresholdPercent, Version)
                VALUES (1, 3, 'knight-break', 1, 70, 0);
                INSERT INTO BattleSkillCooldowns (RoomId, CharacterId, SkillCode, ReadyAtRound)
                VALUES (1, 1, 'knight-break', 4);
                """);

            await db.Database.MigrateAsync();

            var character = await db.Characters.SingleAsync();
            var slot = await db.CharacterSkillSlots.SingleAsync();
            Assert.Equal((2, 0), (character.TalentPoints, character.AttackTalentRank));
            Assert.Null(slot.SkillCode);
            Assert.False(slot.AutoUseEnabled);
            Assert.Empty(await db.CharacterSkillTalents.ToListAsync());
            Assert.Empty(await db.BattleSkillCooldowns.ToListAsync());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ExistingCharactersBecomeSwordfightersWithNewStarterSkills()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-skill-migration-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            await using var db = new GameDbContext(options);
            await db.Database.GetService<IMigrator>().MigrateAsync("20260920050000_AddCombatConsumables");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Characters (Id, UserId, Name, Hp, MaxHp, Attack, Defense, Level, Experience, TalentPoints, AttackTalentRank, DefenseTalentRank, HealthTalentRank, Version) VALUES (1, 1, 'Existing', 80, 100, 20, 5, 3, 0, 2, 0, 0, 0, 0)");

            await db.Database.MigrateAsync();

            var character = await db.Characters.SingleAsync();
            Assert.Equal("swordsman", character.ProfessionCode);
            Assert.Equal(new[] { "sword-slash", "sword-parry" },
                (await db.CharacterSkillSlots.OrderBy(slot => slot.SlotIndex).ToListAsync()).Select(slot => slot.SkillCode));
            Assert.Empty(await db.CharacterSkillTalents.ToListAsync());
            Assert.Equal((80, 3, 2), (character.Hp, character.Level, character.TalentPoints));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
