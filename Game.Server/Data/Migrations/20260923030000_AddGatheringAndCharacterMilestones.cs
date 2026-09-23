using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260923030000_AddGatheringAndCharacterMilestones")]
public sealed class AddGatheringAndCharacterMilestones : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("GatheringLevel", "Characters", "INTEGER", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<bool>("HasParticipatedInRun", "RoomSlots", "INTEGER", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<int>("LastParticipatedMonsterId", "RoomSlots", "INTEGER", nullable: true);
        migrationBuilder.CreateTable(
            name: "CharacterBattleMilestones",
            columns: table => new
            {
                CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                Kind = table.Column<string>(type: "TEXT", nullable: false),
                TargetCode = table.Column<string>(type: "TEXT", nullable: false),
                Count = table.Column<int>(type: "INTEGER", nullable: false),
                FirstAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CharacterBattleMilestones", row => new { row.CharacterId, row.Kind, row.TargetCode });
                table.CheckConstraint("CK_CharacterBattleMilestones_Count", "Count > 0");
            });
        migrationBuilder.CreateTable(
            name: "GatheringTasks",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                UserId = table.Column<int>(type: "INTEGER", nullable: false),
                CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                PointCode = table.Column<string>(type: "TEXT", nullable: false),
                MaterialCode = table.Column<string>(type: "TEXT", nullable: false),
                CycleSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                OutputQuantity = table.Column<int>(type: "INTEGER", nullable: false),
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
                table.PrimaryKey("PK_GatheringTasks", row => row.Id);
                table.CheckConstraint("CK_GatheringTasks_Quantities", "CompletedCycles >= 0 AND TotalQuantity >= 0 AND CycleSeconds > 0 AND OutputQuantity > 0");
            });
        migrationBuilder.CreateIndex("IX_GatheringTasks_CharacterId_Status", "GatheringTasks", new[] { "CharacterId", "Status" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("GatheringTasks");
        migrationBuilder.DropTable("CharacterBattleMilestones");
        migrationBuilder.DropColumn("LastParticipatedMonsterId", "RoomSlots");
        migrationBuilder.DropColumn("HasParticipatedInRun", "RoomSlots");
        migrationBuilder.DropColumn("GatheringLevel", "Characters");
    }
}
