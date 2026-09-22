using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260922020000_AddWeaponTemplateRevision")]
public sealed class AddWeaponTemplateRevision : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<int>(
        name: "TemplateRevision", table: "CharacterWeapons", type: "INTEGER", nullable: false, defaultValue: 0);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn("TemplateRevision", "CharacterWeapons");
}
