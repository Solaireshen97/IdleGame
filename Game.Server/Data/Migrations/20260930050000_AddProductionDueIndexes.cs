using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260930050000_AddProductionDueIndexes")]
public sealed class AddProductionDueIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex("IX_ProductionTasks_Status_NextCycleAtUtc", "ProductionTasks",
            new[] { "Status", "NextCycleAtUtc" });
        migrationBuilder.CreateIndex("IX_ProductionTasks_Status_EndsAtUtc", "ProductionTasks",
            new[] { "Status", "EndsAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_ProductionTasks_Status_NextCycleAtUtc", "ProductionTasks");
        migrationBuilder.DropIndex("IX_ProductionTasks_Status_EndsAtUtc", "ProductionTasks");
    }
}
