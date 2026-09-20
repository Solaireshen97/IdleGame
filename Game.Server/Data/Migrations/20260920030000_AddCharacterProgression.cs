using Game.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260920030000_AddCharacterProgression")]
public partial class AddCharacterProgression : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "Level",
            table: "Characters",
            type: "INTEGER",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<int>(
            name: "Experience",
            table: "Characters",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "TalentPoints",
            table: "Characters",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Level", table: "Characters");
        migrationBuilder.DropColumn(name: "Experience", table: "Characters");
        migrationBuilder.DropColumn(name: "TalentPoints", table: "Characters");
    }
}
