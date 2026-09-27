using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260927010000_RemoveMainControl")]
public sealed class RemoveMainControl : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(name: "IsOwnerAutoEnabled", table: "Rooms",
            type: "INTEGER", nullable: false, defaultValue: false);
        // Preserve the old owner switch even when its designated character was dead or in a rear slot.
        migrationBuilder.Sql("""
            UPDATE Rooms SET IsOwnerAutoEnabled = COALESCE((
                SELECT IsAutoEnabled FROM RoomSlots
                WHERE RoomId = Rooms.Id AND UserId = Rooms.OwnerUserId AND CharacterId IS NOT NULL
                ORDER BY IsMainControl DESC, SlotIndex LIMIT 1), 0);
            UPDATE RoomSlots SET IsAutoEnabled = (
                SELECT IsOwnerAutoEnabled FROM Rooms WHERE Id = RoomSlots.RoomId)
                WHERE CharacterId IS NOT NULL AND UserId = (SELECT OwnerUserId FROM Rooms WHERE Id = RoomSlots.RoomId);
            ALTER TABLE RoomSlots DROP COLUMN IsMainControl;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(name: "IsMainControl", table: "RoomSlots",
            type: "INTEGER", nullable: false, defaultValue: false);
        migrationBuilder.Sql("""
            UPDATE RoomSlots SET IsAutoEnabled = (
                SELECT IsOwnerAutoEnabled FROM Rooms WHERE Id = RoomSlots.RoomId)
                WHERE CharacterId IS NOT NULL AND UserId = (SELECT OwnerUserId FROM Rooms WHERE Id = RoomSlots.RoomId);
            UPDATE RoomSlots SET IsMainControl = 1 WHERE Id IN (
                SELECT (SELECT Id FROM RoomSlots WHERE RoomId = Rooms.Id AND UserId = Rooms.OwnerUserId
                    AND CharacterId IS NOT NULL ORDER BY SlotIndex LIMIT 1) FROM Rooms);
            ALTER TABLE Rooms DROP COLUMN IsOwnerAutoEnabled;
            """);
    }
}
