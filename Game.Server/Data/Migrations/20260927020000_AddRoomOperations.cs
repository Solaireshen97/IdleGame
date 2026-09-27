using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260927020000_AddRoomOperations")]
public sealed class AddRoomOperations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("RoomOperations", columns: table => new
        {
            Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
            RoomId = table.Column<int>(type: "INTEGER", nullable: false),
            UserId = table.Column<int>(type: "INTEGER", nullable: false),
            Kind = table.Column<int>(type: "INTEGER", nullable: false),
            SlotIndex = table.Column<int>(type: "INTEGER", nullable: false),
            CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
            CharacterName = table.Column<string>(type: "TEXT", nullable: false),
            ExpectedTargetCharacterId = table.Column<int>(type: "INTEGER", nullable: true),
            ExpectedSourceSlotIndex = table.Column<int>(type: "INTEGER", nullable: true),
            Status = table.Column<string>(type: "TEXT", nullable: false),
            Error = table.Column<string>(type: "TEXT", nullable: true),
            CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
            FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
            Version = table.Column<int>(type: "INTEGER", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_RoomOperations", operation => operation.Id);
            table.ForeignKey("FK_RoomOperations_Rooms_RoomId", operation => operation.RoomId,
                principalTable: "Rooms", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_RoomOperations_RoomId_Status_Id", "RoomOperations", new[] { "RoomId", "Status", "Id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("RoomOperations");
}
