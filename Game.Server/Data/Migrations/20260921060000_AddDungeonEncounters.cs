using Game.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260921060000_AddDungeonEncounters")]
public partial class AddDungeonEncounters : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("CurrentWaveNumber", "Rooms", type: "INTEGER", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<int>("TotalWaveCount", "Rooms", type: "INTEGER", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<int>("RoomId", "Monsters", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<int>("WaveNumber", "Monsters", type: "INTEGER", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<int>("Position", "Monsters", type: "INTEGER", nullable: false, defaultValue: 1);

        migrationBuilder.Sql("""
            UPDATE Monsters
            SET RoomId = (SELECT Rooms.Id FROM Rooms WHERE Rooms.MonsterId = Monsters.Id LIMIT 1)
            WHERE EXISTS (SELECT 1 FROM Rooms WHERE Rooms.MonsterId = Monsters.Id);
            """);

        migrationBuilder.CreateIndex(
            name: "IX_Monsters_RoomId_WaveNumber_Position",
            table: "Monsters",
            columns: new[] { "RoomId", "WaveNumber", "Position" },
            unique: true,
            filter: "RoomId IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_Monsters_RoomId_WaveNumber_Position", "Monsters");
        migrationBuilder.DropColumn("CurrentWaveNumber", "Rooms");
        migrationBuilder.DropColumn("TotalWaveCount", "Rooms");
        migrationBuilder.DropColumn("RoomId", "Monsters");
        migrationBuilder.DropColumn("WaveNumber", "Monsters");
        migrationBuilder.DropColumn("Position", "Monsters");
    }
}
