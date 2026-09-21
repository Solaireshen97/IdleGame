using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260921110000_AddRegions")]
public partial class AddRegions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "RegionCode", table: "Dungeons",
            type: "TEXT", nullable: false, defaultValue: "");
        migrationBuilder.Sql("UPDATE Dungeons SET RegionCode = 'elwynn' WHERE RegionName = '艾尔文森林';");
        migrationBuilder.Sql("UPDATE Dungeons SET RegionCode = 'legacy' WHERE DungeonKind = 'Legacy';");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "RegionCode", table: "Dungeons");
}
