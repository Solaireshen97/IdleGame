using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260923050000_AddProductionAndAlchemy")]
public sealed class AddProductionAndAlchemy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("AlchemyLevel", "Characters", "INTEGER", nullable: false, defaultValue: 1);
        migrationBuilder.CreateTable(
            name: "ProductionTasks",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                UserId = table.Column<int>(type: "INTEGER", nullable: false),
                CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                RecipeCode = table.Column<string>(type: "TEXT", nullable: false),
                OutputCode = table.Column<string>(type: "TEXT", nullable: false),
                OutputQuantity = table.Column<int>(type: "INTEGER", nullable: false),
                IngredientsJson = table.Column<string>(type: "TEXT", nullable: false),
                CycleSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                EndsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                NextCycleAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                StoppedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                CompletedCycles = table.Column<int>(type: "INTEGER", nullable: false),
                TotalQuantity = table.Column<int>(type: "INTEGER", nullable: false),
                Version = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProductionTasks", row => row.Id);
                table.CheckConstraint("CK_ProductionTasks_Quantities",
                    "CompletedCycles >= 0 AND TotalQuantity >= 0 AND CycleSeconds > 0 AND OutputQuantity > 0");
            });
        migrationBuilder.CreateIndex("IX_ProductionTasks_CharacterId_Status", "ProductionTasks",
            new[] { "CharacterId", "Status" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("ProductionTasks");
        migrationBuilder.DropColumn("AlchemyLevel", "Characters");
    }
}
