using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260923040000_AddRareGatheringOpportunities")]
public sealed class AddRareGatheringOpportunities : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>("IsRare", "GatheringTasks", "INTEGER", nullable: false, defaultValue: false);
        migrationBuilder.CreateTable(
            name: "CharacterGatheringOpportunities",
            columns: table => new
            {
                CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                PointCode = table.Column<string>(type: "TEXT", nullable: false),
                AvailableCount = table.Column<int>(type: "INTEGER", nullable: false),
                EarnedCount = table.Column<int>(type: "INTEGER", nullable: false),
                SpentCount = table.Column<int>(type: "INTEGER", nullable: false),
                Version = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CharacterGatheringOpportunities", row => new { row.CharacterId, row.PointCode });
                table.CheckConstraint("CK_CharacterGatheringOpportunities_Counts",
                    "AvailableCount >= 0 AND EarnedCount >= 0 AND SpentCount >= 0 AND EarnedCount = AvailableCount + SpentCount");
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("CharacterGatheringOpportunities");
        migrationBuilder.DropColumn("IsRare", "GatheringTasks");
    }
}
