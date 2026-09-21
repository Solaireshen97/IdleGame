using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260921050000_AddRewardRuns")]
public partial class AddRewardRuns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("RunSequence", "Rooms", type: "INTEGER", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<int>("Gold", "Users", type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>("Version", "Users", type: "INTEGER", nullable: false, defaultValue: 0);

        migrationBuilder.CreateTable("RewardRuns", table => new
        {
            RoomId = table.Column<int>(type: "INTEGER", nullable: false),
            Sequence = table.Column<int>(type: "INTEGER", nullable: false),
            Status = table.Column<string>(type: "TEXT", nullable: false),
            SettledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
        }, constraints: table => table.PrimaryKey("PK_RewardRuns", row => new { row.RoomId, row.Sequence }));

        migrationBuilder.CreateTable("RewardEvents", table => new
        {
            RoomId = table.Column<int>(type: "INTEGER", nullable: false),
            Sequence = table.Column<int>(type: "INTEGER", nullable: false),
            EventKey = table.Column<string>(type: "TEXT", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_RewardEvents", row => new { row.RoomId, row.Sequence, row.EventKey }));

        migrationBuilder.CreateTable("RewardEntries", table => new
        {
            Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
            RoomId = table.Column<int>(type: "INTEGER", nullable: false),
            Sequence = table.Column<int>(type: "INTEGER", nullable: false),
            EventKey = table.Column<string>(type: "TEXT", nullable: false),
            UserId = table.Column<int>(type: "INTEGER", nullable: false),
            CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
            Kind = table.Column<string>(type: "TEXT", nullable: false),
            Code = table.Column<string>(type: "TEXT", nullable: false),
            Quantity = table.Column<int>(type: "INTEGER", nullable: false),
            WeaponSnapshotJson = table.Column<string>(type: "TEXT", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_RewardEntries", row => row.Id);
            table.CheckConstraint("CK_RewardEntries_Quantity", "Quantity > 0");
        });
        migrationBuilder.CreateIndex("IX_RewardEntries_RoomId_Sequence_UserId", "RewardEntries",
            new[] { "RoomId", "Sequence", "UserId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("RewardEntries");
        migrationBuilder.DropTable("RewardEvents");
        migrationBuilder.DropTable("RewardRuns");
        migrationBuilder.DropColumn("RunSequence", "Rooms");
        migrationBuilder.DropColumn("Gold", "Users");
        migrationBuilder.DropColumn("Version", "Users");
    }
}
