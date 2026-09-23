using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260923070000_AddOperationPotion")]
public sealed class AddOperationPotion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "BattleOperationPotionStates",
            columns: table => new
            {
                RoomId = table.Column<int>(type: "INTEGER", nullable: false),
                RunSequence = table.Column<int>(type: "INTEGER", nullable: false),
                CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                ItemCode = table.Column<string>(type: "TEXT", nullable: true),
                AttackPercent = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BattleOperationPotionStates", row => new { row.RoomId, row.RunSequence, row.CharacterId });
                table.CheckConstraint("CK_BattleOperationPotionStates_AttackPercent", "AttackPercent BETWEEN 0 AND 100");
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("BattleOperationPotionStates");
}
