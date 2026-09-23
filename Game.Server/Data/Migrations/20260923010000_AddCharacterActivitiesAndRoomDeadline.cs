using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260923010000_AddCharacterActivitiesAndRoomDeadline")]
public sealed class AddCharacterActivitiesAndRoomDeadline : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>("StartedAtUtc", "Rooms", "TEXT", nullable: true);
        migrationBuilder.AddColumn<DateTime>("ExpiresAtUtc", "Rooms", "TEXT", nullable: true);
        migrationBuilder.AddColumn<DateTime>("ClosedAtUtc", "Rooms", "TEXT", nullable: true);
        migrationBuilder.CreateTable(
            name: "CharacterActivities",
            columns: table => new
            {
                CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                Kind = table.Column<string>(type: "TEXT", nullable: false),
                SourceId = table.Column<int>(type: "INTEGER", nullable: false),
                StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                EndsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_CharacterActivities", row => row.CharacterId));
        migrationBuilder.CreateIndex("IX_CharacterActivities_Kind_SourceId", "CharacterActivities", new[] { "Kind", "SourceId" });
        migrationBuilder.Sql("""
            UPDATE Rooms
            SET StartedAtUtc = strftime('%Y-%m-%d %H:%M:%f', 'now'),
                ExpiresAtUtc = strftime('%Y-%m-%d %H:%M:%f', 'now', '+12 hours')
            WHERE IsRepeatBattle = 1;
            """);
        migrationBuilder.Sql("""
            INSERT INTO CharacterActivities (CharacterId, Kind, SourceId, StartedAtUtc, EndsAtUtc)
            SELECT RoomSlots.CharacterId, 'Battle', Rooms.Id,
                   COALESCE(Rooms.StartedAtUtc, strftime('%Y-%m-%d %H:%M:%f', 'now')),
                   Rooms.ExpiresAtUtc
            FROM RoomSlots JOIN Rooms ON Rooms.Id = RoomSlots.RoomId
            WHERE RoomSlots.CharacterId IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("CharacterActivities");
        migrationBuilder.DropColumn("StartedAtUtc", "Rooms");
        migrationBuilder.DropColumn("ExpiresAtUtc", "Rooms");
        migrationBuilder.DropColumn("ClosedAtUtc", "Rooms");
    }
}
