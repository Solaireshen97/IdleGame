using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260927030000_AddCharacterQuickSkillCast")]
public sealed class AddCharacterQuickSkillCast : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<bool>(name: "IsQuickSkillCastEnabled", table: "Characters", type: "INTEGER", nullable: false, defaultValue: false);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("ALTER TABLE Characters DROP COLUMN IsQuickSkillCastEnabled;");
}
