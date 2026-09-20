using Game.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260921000000_AddSkillTalentTree")]
public partial class AddSkillTalentTree : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CharacterSkillTalents",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                NodeCode = table.Column<string>(type: "TEXT", nullable: false),
                PointsSpent = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CharacterSkillTalents", x => x.Id);
                table.CheckConstraint("CK_CharacterSkillTalents_PointsSpent", "PointsSpent > 0");
            });

        migrationBuilder.CreateIndex(
            name: "IX_CharacterSkillTalents_CharacterId_NodeCode",
            table: "CharacterSkillTalents",
            columns: new[] { "CharacterId", "NodeCode" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "CharacterSkillTalents");
}
