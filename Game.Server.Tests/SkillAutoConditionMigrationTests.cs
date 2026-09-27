using Game.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Game.Server.Tests;

public class SkillAutoConditionMigrationTests
{
    [Fact]
    public async Task MigrationPreservesExistingSkillAutoSettingsAndLeavesDefaultConditionSelected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"idlegame-skill-auto-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            await using var db = new GameDbContext(options);
            await db.Database.GetService<IMigrator>().MigrateAsync("20260927030000_AddCharacterQuickSkillCast");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO Users (Id, UserName, PasswordHash, ActiveCharacterId) VALUES (1, 'owner', 'x', 1);
                INSERT INTO Characters (Id, UserId, Name, ProfessionCode, Level, Hp, MaxHp, Attack, Version)
                VALUES (1, 1, 'Hunter', 'hunter', 10, 100, 100, 20, 0);
                INSERT INTO CharacterSkillSlots (CharacterId, SlotIndex, SkillCode, AutoUseEnabled, AutoHpThresholdPercent, Version)
                VALUES (1, 1, 'hunter-rapid-volley', 1, 42, 7), (1, 2, 'hunter-field-mend', 0, 65, 2);
                """);

            await db.Database.MigrateAsync();

            var slots = await db.CharacterSkillSlots.OrderBy(slot => slot.SlotIndex).ToListAsync();
            Assert.All(slots, slot => Assert.Null(slot.AutoConditionOverride));
            Assert.Equal(("hunter-rapid-volley", true, 42, 7),
                (slots[0].SkillCode, slots[0].AutoUseEnabled, slots[0].AutoHpThresholdPercent, slots[0].Version));
            Assert.Equal(("hunter-field-mend", false, 65, 2),
                (slots[1].SkillCode, slots[1].AutoUseEnabled, slots[1].AutoHpThresholdPercent, slots[1].Version));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
