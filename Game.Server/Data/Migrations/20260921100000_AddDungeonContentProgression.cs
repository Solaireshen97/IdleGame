using Game.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260921100000_AddDungeonContentProgression")]
public partial class AddDungeonContentProgression : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "RegionName", table: "Dungeons",
            type: "TEXT", nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>(name: "DungeonKind", table: "Dungeons",
            type: "TEXT", nullable: false, defaultValue: "Hunt");
        migrationBuilder.AddColumn<string>(name: "Description", table: "Dungeons",
            type: "TEXT", nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<int>(name: "MinimumLevel", table: "Dungeons",
            type: "INTEGER", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<int>(name: "RecommendedLevel", table: "Dungeons",
            type: "INTEGER", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<bool>(name: "IsVisible", table: "Dungeons",
            type: "INTEGER", nullable: false, defaultValue: true);

        migrationBuilder.AddColumn<string>(name: "RewardProfileCode", table: "Monsters",
            type: "TEXT", nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<bool>(name: "IsBoss", table: "Monsters",
            type: "INTEGER", nullable: false, defaultValue: false);

        migrationBuilder.Sql("UPDATE Dungeons SET IsVisible = 0, RegionName = '旧版测试区域', DungeonKind = 'Legacy' WHERE Code IN ('slime-field', 'goblin-camp', 'wolf-forest');");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "IsBoss", table: "Monsters");
        migrationBuilder.DropColumn(name: "RewardProfileCode", table: "Monsters");
        migrationBuilder.DropColumn(name: "IsVisible", table: "Dungeons");
        migrationBuilder.DropColumn(name: "RecommendedLevel", table: "Dungeons");
        migrationBuilder.DropColumn(name: "MinimumLevel", table: "Dungeons");
        migrationBuilder.DropColumn(name: "Description", table: "Dungeons");
        migrationBuilder.DropColumn(name: "DungeonKind", table: "Dungeons");
        migrationBuilder.DropColumn(name: "RegionName", table: "Dungeons");
    }
}
