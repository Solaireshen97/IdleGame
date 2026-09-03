using Game.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260612060000_AddRoomSlots")]
public partial class AddRoomSlots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "OwnerUserId", table: "Rooms", type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>(name: "SlotCount", table: "Rooms", type: "INTEGER", nullable: false, defaultValue: 5);
        migrationBuilder.Sql("CREATE TABLE __RoomSlotMigrationGuard (Id INTEGER NOT NULL CHECK (Id = 0));");
        migrationBuilder.Sql("INSERT INTO __RoomSlotMigrationGuard (Id) SELECT 1 WHERE EXISTS (SELECT 1 FROM RoomMembers GROUP BY RoomId HAVING COUNT(*) > 5);");
        migrationBuilder.Sql("DROP TABLE __RoomSlotMigrationGuard;");
        migrationBuilder.CreateTable(
            name: "RoomSlots",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                RoomId = table.Column<int>(type: "INTEGER", nullable: false),
                SlotIndex = table.Column<int>(type: "INTEGER", nullable: false),
                CharacterId = table.Column<int>(type: "INTEGER", nullable: true),
                UserId = table.Column<int>(type: "INTEGER", nullable: true),
                IsMainControl = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_RoomSlots", x => x.Id));
        migrationBuilder.Sql("UPDATE Rooms SET OwnerUserId = COALESCE((SELECT UserId FROM RoomMembers WHERE RoomMembers.RoomId = Rooms.Id AND IsOwner = 1 ORDER BY Id LIMIT 1), 0), SlotCount = 5;");
        migrationBuilder.Sql("INSERT INTO RoomSlots (RoomId, SlotIndex, CharacterId, UserId, IsMainControl) SELECT Id, 1, NULL, NULL, 0 FROM Rooms;");
        migrationBuilder.Sql("INSERT INTO RoomSlots (RoomId, SlotIndex, CharacterId, UserId, IsMainControl) SELECT Id, 2, NULL, NULL, 0 FROM Rooms;");
        migrationBuilder.Sql("INSERT INTO RoomSlots (RoomId, SlotIndex, CharacterId, UserId, IsMainControl) SELECT Id, 3, NULL, NULL, 0 FROM Rooms;");
        migrationBuilder.Sql("INSERT INTO RoomSlots (RoomId, SlotIndex, CharacterId, UserId, IsMainControl) SELECT Id, 4, NULL, NULL, 0 FROM Rooms;");
        migrationBuilder.Sql("INSERT INTO RoomSlots (RoomId, SlotIndex, CharacterId, UserId, IsMainControl) SELECT Id, 5, NULL, NULL, 0 FROM Rooms;");
        migrationBuilder.Sql("UPDATE RoomSlots SET CharacterId = (SELECT CharacterId FROM (SELECT RoomId, CharacterId, ROW_NUMBER() OVER (PARTITION BY RoomId ORDER BY IsOwner DESC, Id) AS MemberIndex FROM RoomMembers) Members WHERE Members.RoomId = RoomSlots.RoomId AND Members.MemberIndex = RoomSlots.SlotIndex), UserId = (SELECT UserId FROM (SELECT RoomId, UserId, ROW_NUMBER() OVER (PARTITION BY RoomId ORDER BY IsOwner DESC, Id) AS MemberIndex FROM RoomMembers) Members WHERE Members.RoomId = RoomSlots.RoomId AND Members.MemberIndex = RoomSlots.SlotIndex);");
        migrationBuilder.Sql("UPDATE RoomSlots SET IsMainControl = 1 WHERE SlotIndex = 1 AND CharacterId IS NOT NULL;");
        migrationBuilder.CreateIndex(name: "IX_RoomSlots_CharacterId", table: "RoomSlots", column: "CharacterId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_RoomSlots_RoomId", table: "RoomSlots", column: "RoomId");
        migrationBuilder.CreateIndex(name: "IX_RoomSlots_RoomId_SlotIndex", table: "RoomSlots", columns: new[] { "RoomId", "SlotIndex" }, unique: true);
        migrationBuilder.DropTable(name: "RoomMembers");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "RoomSlots");
        migrationBuilder.DropColumn(name: "OwnerUserId", table: "Rooms");
        migrationBuilder.DropColumn(name: "SlotCount", table: "Rooms");
    }
}
