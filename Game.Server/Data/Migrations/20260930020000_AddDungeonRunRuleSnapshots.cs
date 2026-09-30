using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260930020000_AddDungeonRunRuleSnapshots")]
public sealed class AddDungeonRunRuleSnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable(
        name: "DungeonRunRuleSnapshots",
        columns: table => new
        {
            RoomId = table.Column<int>(type: "INTEGER", nullable: false),
            Revision = table.Column<string>(type: "TEXT", nullable: false),
            DefinitionJson = table.Column<string>(type: "TEXT", nullable: false)
        },
        constraints: table =>
        {
            table.PrimaryKey("PK_DungeonRunRuleSnapshots", entry => entry.RoomId);
            table.ForeignKey("FK_DungeonRunRuleSnapshots_Rooms_RoomId", entry => entry.RoomId,
                "Rooms", "Id", onDelete: ReferentialAction.Cascade);
        });
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("DungeonRunRuleSnapshots");
}
