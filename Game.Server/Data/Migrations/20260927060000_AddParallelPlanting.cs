using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260927060000_AddParallelPlanting")]
public sealed class AddParallelPlanting : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE GatheringTasks SET Status = 'Stopped', StoppedAtUtc = CURRENT_TIMESTAMP, Version = Version + 1 WHERE Status = 'Running';");
        migrationBuilder.Sql("UPDATE ProductionTasks SET Status = 'Stopped', StoppedAtUtc = CURRENT_TIMESTAMP, Version = Version + 1 WHERE Status = 'Running';");
        migrationBuilder.Sql("DELETE FROM CharacterActivities WHERE Kind IN ('Gathering', 'Production');");
        migrationBuilder.AddColumn<int>("TargetCycles", "ProductionTasks", "INTEGER", nullable: true);
        migrationBuilder.AddColumn<string>("RequestId", "ProductionTasks", "TEXT", nullable: true);
        migrationBuilder.CreateIndex("IX_ProductionTasks_CharacterId", "ProductionTasks", "CharacterId", unique: true, filter: "Status = 'Running'");
        migrationBuilder.CreateIndex("IX_ProductionTasks_CharacterId_RequestId", "ProductionTasks", new[] { "CharacterId", "RequestId" }, unique: true, filter: "RequestId IS NOT NULL");
        migrationBuilder.CreateTable("CharacterGardenPlots", columns: table => new
        {
            Id = table.Column<int>("INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
            CharacterId = table.Column<int>("INTEGER", nullable: false),
            PlotIndex = table.Column<int>("INTEGER", nullable: false),
            Version = table.Column<int>("INTEGER", nullable: false),
            PlantCode = table.Column<string>("TEXT", nullable: true),
            SeedCode = table.Column<string>("TEXT", nullable: true),
            MaterialCode = table.Column<string>("TEXT", nullable: true),
            PlantName = table.Column<string>("TEXT", nullable: true),
            HarvestQuantity = table.Column<int>("INTEGER", nullable: false),
            GrowthSeconds = table.Column<int>("INTEGER", nullable: false),
            PlantedAtUtc = table.Column<DateTime>("TEXT", nullable: true),
            MaturesAtUtc = table.Column<DateTime>("TEXT", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_CharacterGardenPlots", row => row.Id);
            table.CheckConstraint("CK_CharacterGardenPlots_Values", "PlotIndex >= 0 AND PlotIndex < 4 AND HarvestQuantity >= 0 AND GrowthSeconds >= 0");
        });
        migrationBuilder.CreateIndex("IX_CharacterGardenPlots_CharacterId_PlotIndex", "CharacterGardenPlots", new[] { "CharacterId", "PlotIndex" }, unique: true);
        migrationBuilder.CreateTable("LogisticsRequests", columns: table => new
        {
            CharacterId = table.Column<int>("INTEGER", nullable: false),
            RequestId = table.Column<string>("TEXT", nullable: false),
            Kind = table.Column<string>("TEXT", nullable: false),
            Fingerprint = table.Column<string>("TEXT", nullable: false),
            CompletedAtUtc = table.Column<DateTime>("TEXT", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_LogisticsRequests", row => new { row.CharacterId, row.RequestId }));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("CharacterGardenPlots");
        migrationBuilder.DropTable("LogisticsRequests");
        migrationBuilder.DropIndex("IX_ProductionTasks_CharacterId", "ProductionTasks");
        migrationBuilder.DropIndex("IX_ProductionTasks_CharacterId_RequestId", "ProductionTasks");
        migrationBuilder.Sql("ALTER TABLE ProductionTasks DROP COLUMN TargetCycles;");
        migrationBuilder.Sql("ALTER TABLE ProductionTasks DROP COLUMN RequestId;");
    }
}
