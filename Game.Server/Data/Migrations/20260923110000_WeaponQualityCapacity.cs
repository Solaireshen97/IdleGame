using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260923110000_WeaponQualityCapacity")]
public sealed class WeaponQualityCapacity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "QualityRank", table: "CharacterWeapons",
            type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.Sql("""
            UPDATE CharacterWeapons
            SET QualityRank = MIN(3, COALESCE((
                SELECT SUM(QualityBonusLevel) FROM CharacterWeaponSkills
                WHERE WeaponId = CharacterWeapons.Id), 0))
            """);
        migrationBuilder.Sql("""
            CREATE TABLE CharacterWeaponSkills_new (
                Id INTEGER NOT NULL CONSTRAINT PK_CharacterWeaponSkills PRIMARY KEY AUTOINCREMENT,
                WeaponId INTEGER NOT NULL,
                SlotIndex INTEGER NOT NULL,
                SkillCode TEXT NOT NULL,
                Level INTEGER NOT NULL,
                BaseLevel INTEGER NOT NULL,
                QualityBonusLevel INTEGER NOT NULL,
                EnhancementLevel INTEGER NOT NULL,
                SpentFragments INTEGER NULL,
                CONSTRAINT CK_CharacterWeaponSkills_Level CHECK (Level BETWEEN 1 AND 20),
                CONSTRAINT CK_CharacterWeaponSkills_Progression CHECK (BaseLevel BETWEEN 1 AND 20 AND QualityBonusLevel = 0 AND EnhancementLevel BETWEEN 0 AND 6 AND Level = BaseLevel + EnhancementLevel),
                CONSTRAINT CK_CharacterWeaponSkills_Slot CHECK (SlotIndex BETWEEN 1 AND 3),
                CONSTRAINT FK_CharacterWeaponSkills_CharacterWeapons_WeaponId FOREIGN KEY (WeaponId) REFERENCES CharacterWeapons (Id) ON DELETE CASCADE
            );
            INSERT INTO CharacterWeaponSkills_new (Id, WeaponId, SlotIndex, SkillCode, Level, BaseLevel, QualityBonusLevel, EnhancementLevel, SpentFragments)
            SELECT Id, WeaponId, SlotIndex, SkillCode, BaseLevel + EnhancementLevel, BaseLevel, 0, EnhancementLevel, SpentFragments
            FROM CharacterWeaponSkills;
            DROP TABLE CharacterWeaponSkills;
            ALTER TABLE CharacterWeaponSkills_new RENAME TO CharacterWeaponSkills;
            CREATE UNIQUE INDEX IX_CharacterWeaponSkills_WeaponId_SlotIndex ON CharacterWeaponSkills (WeaponId, SlotIndex);
            CREATE UNIQUE INDEX IX_CharacterWeaponSkills_WeaponId_SkillCode ON CharacterWeaponSkills (WeaponId, SkillCode);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE CharacterWeaponSkills_old (
                Id INTEGER NOT NULL CONSTRAINT PK_CharacterWeaponSkills PRIMARY KEY AUTOINCREMENT,
                WeaponId INTEGER NOT NULL, SlotIndex INTEGER NOT NULL, SkillCode TEXT NOT NULL,
                Level INTEGER NOT NULL, BaseLevel INTEGER NOT NULL,
                QualityBonusLevel INTEGER NOT NULL, EnhancementLevel INTEGER NOT NULL,
                SpentFragments INTEGER NULL,
                CONSTRAINT CK_CharacterWeaponSkills_Level CHECK (Level BETWEEN 1 AND 20),
                CONSTRAINT CK_CharacterWeaponSkills_Progression CHECK (BaseLevel BETWEEN 1 AND 20 AND QualityBonusLevel BETWEEN 0 AND 3 AND EnhancementLevel BETWEEN 0 AND 3 AND Level = BaseLevel + QualityBonusLevel + EnhancementLevel),
                CONSTRAINT CK_CharacterWeaponSkills_Slot CHECK (SlotIndex BETWEEN 1 AND 3),
                CONSTRAINT FK_CharacterWeaponSkills_CharacterWeapons_WeaponId FOREIGN KEY (WeaponId) REFERENCES CharacterWeapons (Id) ON DELETE CASCADE
            );
            INSERT INTO CharacterWeaponSkills_old (Id, WeaponId, SlotIndex, SkillCode, Level, BaseLevel, QualityBonusLevel, EnhancementLevel, SpentFragments)
            SELECT Id, WeaponId, SlotIndex, SkillCode, BaseLevel + MIN(EnhancementLevel, 3), BaseLevel, 0, MIN(EnhancementLevel, 3), SpentFragments
            FROM CharacterWeaponSkills;
            DROP TABLE CharacterWeaponSkills;
            ALTER TABLE CharacterWeaponSkills_old RENAME TO CharacterWeaponSkills;
            CREATE UNIQUE INDEX IX_CharacterWeaponSkills_WeaponId_SlotIndex ON CharacterWeaponSkills (WeaponId, SlotIndex);
            CREATE UNIQUE INDEX IX_CharacterWeaponSkills_WeaponId_SkillCode ON CharacterWeaponSkills (WeaponId, SkillCode);
            """);
        migrationBuilder.DropColumn(name: "QualityRank", table: "CharacterWeapons");
    }
}
