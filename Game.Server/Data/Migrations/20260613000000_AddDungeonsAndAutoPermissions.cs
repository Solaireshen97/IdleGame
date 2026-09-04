using Game.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260613000000_AddDungeonsAndAutoPermissions")]
public partial class AddDungeonsAndAutoPermissions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Dungeons",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                Code = table.Column<string>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                MonsterName = table.Column<string>(type: "TEXT", nullable: false),
                MonsterMaxHp = table.Column<int>(type: "INTEGER", nullable: false),
                MonsterAttack = table.Column<int>(type: "INTEGER", nullable: false),
                MonsterDefense = table.Column<int>(type: "INTEGER", nullable: false),
                SlotCount = table.Column<int>(type: "INTEGER", nullable: false),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Dungeons", x => x.Id));

        migrationBuilder.Sql("INSERT INTO Dungeons (Id, Code, Name, MonsterName, MonsterMaxHp, MonsterAttack, MonsterDefense, SlotCount, SortOrder) VALUES (1, 'slime-field', '史莱姆平原', 'Slime', 50, 8, 2, 5, 1), (2, 'goblin-camp', '哥布林营地', 'Goblin', 80, 12, 4, 5, 2), (3, 'wolf-forest', '狼群森林', 'Wolf', 65, 15, 3, 5, 3);");

        migrationBuilder.AddColumn<int>(name: "DungeonId", table: "Rooms", type: "INTEGER", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<bool>(name: "IsSelfTeamPreparationTimeoutEnabled", table: "Rooms", type: "INTEGER", nullable: false, defaultValue: true);
        migrationBuilder.Sql("UPDATE Rooms SET DungeonId = CASE (SELECT Name FROM Monsters WHERE Monsters.Id = Rooms.MonsterId) WHEN 'Goblin' THEN 2 WHEN 'Wolf' THEN 3 ELSE 1 END;");
        migrationBuilder.Sql("UPDATE RoomSlots SET IsAutoEnabled = 0;");

        migrationBuilder.CreateTable(
            name: "UserDungeonClears",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                UserId = table.Column<int>(type: "INTEGER", nullable: false),
                DungeonId = table.Column<int>(type: "INTEGER", nullable: false),
                ClearedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_UserDungeonClears", x => x.Id));

        migrationBuilder.CreateIndex(name: "IX_Dungeons_Code", table: "Dungeons", column: "Code", unique: true);
        migrationBuilder.CreateIndex(name: "IX_UserDungeonClears_UserId_DungeonId", table: "UserDungeonClears", columns: new[] { "UserId", "DungeonId" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "UserDungeonClears");
        migrationBuilder.DropTable(name: "Dungeons");
        migrationBuilder.DropColumn(name: "DungeonId", table: "Rooms");
        migrationBuilder.DropColumn(name: "IsSelfTeamPreparationTimeoutEnabled", table: "Rooms");
    }
}
