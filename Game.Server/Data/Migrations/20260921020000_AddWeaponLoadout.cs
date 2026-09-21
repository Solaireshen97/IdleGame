using Game.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260921020000_AddWeaponLoadout")]
public partial class AddWeaponLoadout : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CharacterWeapons",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                WeaponCode = table.Column<string>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                Element = table.Column<string>(type: "TEXT", nullable: false),
                Attack = table.Column<int>(type: "INTEGER", nullable: false),
                MaxHp = table.Column<int>(type: "INTEGER", nullable: false),
                EquippedSlotIndex = table.Column<int>(type: "INTEGER", nullable: true),
                Version = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CharacterWeapons", x => x.Id);
                table.CheckConstraint("CK_CharacterWeapons_Stats", "Attack >= 0 AND MaxHp > 0");
                table.CheckConstraint("CK_CharacterWeapons_Slot", "EquippedSlotIndex IS NULL OR EquippedSlotIndex BETWEEN 1 AND 10");
            });

        migrationBuilder.CreateIndex(
            name: "IX_CharacterWeapons_CharacterId_EquippedSlotIndex",
            table: "CharacterWeapons",
            columns: new[] { "CharacterId", "EquippedSlotIndex" },
            unique: true,
            filter: "EquippedSlotIndex IS NOT NULL");

        // Preserve every existing character's combat stats in their first main weapon.
        migrationBuilder.Sql("""
            INSERT INTO CharacterWeapons (CharacterId, WeaponCode, Name, Element, Attack, MaxHp, EquippedSlotIndex, Version)
            SELECT Id,
                   CASE WHEN ProfessionCode = 'cleric' THEN 'lumen-staff' ELSE 'ember-blade' END,
                   CASE WHEN ProfessionCode = 'cleric' THEN '晨光法杖' ELSE '余烬长剑' END,
                   CASE WHEN ProfessionCode = 'cleric' THEN 'Light' ELSE 'Fire' END,
                   Attack, MaxHp, 1, 0
            FROM Characters;
            """);
        migrationBuilder.Sql("""
            INSERT INTO CharacterWeapons (CharacterId, WeaponCode, Name, Element, Attack, MaxHp, EquippedSlotIndex, Version)
            SELECT Id, 'tide-saber', '潮汐弯刀', 'Water', 8, 24, NULL, 0 FROM Characters;
            """);
        migrationBuilder.Sql("""
            INSERT INTO CharacterWeapons (CharacterId, WeaponCode, Name, Element, Attack, MaxHp, EquippedSlotIndex, Version)
            SELECT Id, 'gale-bow', '疾风短弓', 'Wind', 7, 28, NULL, 0 FROM Characters;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("CharacterWeapons");
}
