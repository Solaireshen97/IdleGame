using Game.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260921040000_AddWeaponSkills")]
public partial class AddWeaponSkills : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(name: "WeaponAttackBonusPercent", table: "Characters",
            type: "TEXT", nullable: false, defaultValue: 0m);
        migrationBuilder.AddColumn<decimal>(name: "WeaponHealthBonusPercent", table: "Characters",
            type: "TEXT", nullable: false, defaultValue: 0m);
        migrationBuilder.AddColumn<decimal>(name: "WeaponCriticalChancePercent", table: "Characters",
            type: "TEXT", nullable: false, defaultValue: 0m);

        migrationBuilder.CreateTable(
            name: "CharacterWeaponSkills",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                WeaponId = table.Column<int>(type: "INTEGER", nullable: false),
                SlotIndex = table.Column<int>(type: "INTEGER", nullable: false),
                SkillCode = table.Column<string>(type: "TEXT", nullable: false),
                Level = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CharacterWeaponSkills", row => row.Id);
                table.CheckConstraint("CK_CharacterWeaponSkills_Level", "Level BETWEEN 1 AND 20");
                table.CheckConstraint("CK_CharacterWeaponSkills_Slot", "SlotIndex BETWEEN 1 AND 3");
                table.ForeignKey("FK_CharacterWeaponSkills_CharacterWeapons_WeaponId", row => row.WeaponId,
                    "CharacterWeapons", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex(name: "IX_CharacterWeaponSkills_WeaponId_SlotIndex",
            table: "CharacterWeaponSkills", columns: new[] { "WeaponId", "SlotIndex" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_CharacterWeaponSkills_WeaponId_SkillCode",
            table: "CharacterWeaponSkills", columns: new[] { "WeaponId", "SkillCode" }, unique: true);

        // An unequipped matching-element weapon lets existing characters try level stacking.
        migrationBuilder.Sql("""
            INSERT INTO CharacterWeapons (CharacterId, WeaponCode, Name, Element, Attack, MaxHp, EquippedSlotIndex, Version)
            SELECT Id,
                   CASE WHEN ProfessionCode = 'cleric' THEN 'light-scepter' ELSE 'cinder-knife' END,
                   CASE WHEN ProfessionCode = 'cleric' THEN '晨辉短杖' ELSE '余烬短剑' END,
                   CASE WHEN ProfessionCode = 'cleric' THEN 'Light' ELSE 'Fire' END,
                   6, 18, NULL, 0
            FROM Characters;
            """);

        // Existing starter weapons gain the same skills as newly created instances.
        migrationBuilder.Sql("""
            INSERT INTO CharacterWeaponSkills (WeaponId, SlotIndex, SkillCode, Level)
            SELECT Id, 1,
                   CASE WHEN WeaponCode IN ('ember-blade', 'cinder-knife', 'stone-hammer') THEN 'weapon-attack'
                        WHEN WeaponCode IN ('lumen-staff', 'light-scepter', 'tide-saber') THEN 'weapon-health'
                        ELSE 'weapon-critical' END,
                   2
            FROM CharacterWeapons
            WHERE WeaponCode IN ('ember-blade', 'cinder-knife', 'lumen-staff', 'light-scepter',
                'tide-saber', 'gale-bow', 'stone-hammer', 'dusk-dagger');
            """);
        migrationBuilder.Sql("""
            INSERT INTO CharacterWeaponSkills (WeaponId, SlotIndex, SkillCode, Level)
            SELECT Id, 2,
                   CASE WHEN WeaponCode = 'stone-hammer' THEN 'weapon-health' ELSE 'weapon-attack' END,
                   1
            FROM CharacterWeapons WHERE WeaponCode IN ('stone-hammer', 'dusk-dagger');
            """);
        migrationBuilder.Sql("""
            UPDATE Characters SET WeaponAttackBonusPercent = 4
            WHERE EXISTS (SELECT 1 FROM CharacterWeapons w WHERE w.CharacterId = Characters.Id
                AND w.EquippedSlotIndex = 1 AND w.WeaponCode = 'ember-blade');
            UPDATE Characters SET WeaponHealthBonusPercent = 6
            WHERE EXISTS (SELECT 1 FROM CharacterWeapons w WHERE w.CharacterId = Characters.Id
                AND w.EquippedSlotIndex = 1 AND w.WeaponCode = 'lumen-staff');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DELETE FROM CharacterWeapons WHERE WeaponCode IN ('cinder-knife', 'light-scepter');");
        migrationBuilder.DropTable("CharacterWeaponSkills");
        migrationBuilder.DropColumn("WeaponAttackBonusPercent", "Characters");
        migrationBuilder.DropColumn("WeaponHealthBonusPercent", "Characters");
        migrationBuilder.DropColumn("WeaponCriticalChancePercent", "Characters");
    }
}
