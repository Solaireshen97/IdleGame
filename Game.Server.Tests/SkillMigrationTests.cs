using Game.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public class SkillMigrationTests
{
    [Fact]
    public async Task ExistingCharactersBecomeKnightsWithStarterSkills()
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
            Assert.Equal("knight", character.ProfessionCode);
            Assert.Equal(new[] { "knight-strike", "knight-guard" },
                (await db.CharacterSkillSlots.OrderBy(slot => slot.SlotIndex).ToListAsync()).Select(slot => slot.SkillCode));
            Assert.Equal((80, 3, 2), (character.Hp, character.Level, character.TalentPoints));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
