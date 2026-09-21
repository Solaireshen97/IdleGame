using Game.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260921030000_AddElementDamage")]
public partial class AddElementDamage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "MonsterElement",
            table: "Dungeons",
            type: "TEXT",
            nullable: false,
            defaultValue: "Wind");

        migrationBuilder.AddColumn<string>(
            name: "Element",
            table: "Monsters",
            type: "TEXT",
            nullable: false,
            defaultValue: "Wind");

        migrationBuilder.Sql("UPDATE Dungeons SET MonsterElement = 'Earth' WHERE Code = 'goblin-camp';");
        migrationBuilder.Sql("UPDATE Dungeons SET MonsterElement = 'Water' WHERE Code = 'wolf-forest';");
        migrationBuilder.Sql("""
            UPDATE Monsters
            SET Element = COALESCE(
                (SELECT d.MonsterElement FROM Rooms r JOIN Dungeons d ON d.Id = r.DungeonId WHERE r.MonsterId = Monsters.Id LIMIT 1),
                'Wind');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "MonsterElement", table: "Dungeons");
        migrationBuilder.DropColumn(name: "Element", table: "Monsters");
    }
}
