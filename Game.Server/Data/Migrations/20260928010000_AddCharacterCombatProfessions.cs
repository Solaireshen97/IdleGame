using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260928010000_AddCharacterCombatProfessions")]
public sealed class AddCharacterCombatProfessions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CharacterCombatProfessions",
            columns: table => new
            {
                CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                ProfessionCode = table.Column<string>(type: "TEXT", nullable: false),
                Level = table.Column<int>(type: "INTEGER", nullable: false),
                Experience = table.Column<int>(type: "INTEGER", nullable: false),
                SkillLoadoutJson = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CharacterCombatProfessions", row => new { row.CharacterId, row.ProfessionCode });
                table.ForeignKey("FK_CharacterCombatProfessions_Characters_CharacterId", row => row.CharacterId,
                    "Characters", "Id", onDelete: ReferentialAction.Cascade);
                table.CheckConstraint("CK_CharacterCombatProfessions_Progress", "Level BETWEEN 1 AND 30 AND Experience >= 0");
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("CharacterCombatProfessions");
}
