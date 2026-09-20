using Game.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260920040000_AddCharacterTalents")]
public partial class AddCharacterTalents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "AttackTalentRank", table: "Characters", type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>(name: "DefenseTalentRank", table: "Characters", type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>(name: "HealthTalentRank", table: "Characters", type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>(name: "Version", table: "Characters", type: "INTEGER", nullable: false, defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "AttackTalentRank", table: "Characters");
        migrationBuilder.DropColumn(name: "DefenseTalentRank", table: "Characters");
        migrationBuilder.DropColumn(name: "HealthTalentRank", table: "Characters");
        migrationBuilder.DropColumn(name: "Version", table: "Characters");
    }
}
