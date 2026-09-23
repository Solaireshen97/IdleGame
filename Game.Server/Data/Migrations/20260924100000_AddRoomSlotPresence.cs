using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260924100000_AddRoomSlotPresence")]
public sealed class AddRoomSlotPresence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(name: "LastSeenAtUtc", table: "RoomSlots",
            type: "TEXT", nullable: true);
        migrationBuilder.Sql("UPDATE RoomSlots SET LastSeenAtUtc = CURRENT_TIMESTAMP WHERE CharacterId IS NOT NULL;");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "LastSeenAtUtc", table: "RoomSlots");
}
