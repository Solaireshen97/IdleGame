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
            db.Users.Add(new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 });
            db.Characters.Add(new Character
            {
                Id = 1, UserId = 1, Name = "Knight", ProfessionCode = "knight", Level = 3,
                TalentPoints = 0, AttackTalentRank = 1, Hp = 100, MaxHp = 100, Attack = 20, Defense = 5
            });
            db.CharacterSkillTalents.Add(new CharacterSkillTalent
            {
                CharacterId = 1, NodeCode = "knight-vanguard", PointsSpent = 1
            });
            db.CharacterSkillSlots.Add(new CharacterSkillSlot
            {
                CharacterId = 1, SlotIndex = 3, SkillCode = "knight-break", AutoUseEnabled = true
            });
            db.BattleSkillCooldowns.Add(new BattleSkillCooldown
            {
                RoomId = 1, CharacterId = 1, SkillCode = "knight-break", ReadyAtRound = 4
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await db.Database.MigrateAsync();

            var character = await db.Characters.SingleAsync();
            var slot = await db.CharacterSkillSlots.SingleAsync();
            Assert.Equal((1, 1), (character.TalentPoints, character.AttackTalentRank));
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
            Assert.Empty(await db.CharacterSkillTalents.ToListAsync());
            Assert.Equal((80, 3, 2), (character.Hp, character.Level, character.TalentPoints));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
