using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260922030000_AddWeaponEconomyTracking")]
public sealed class AddWeaponEconomyTracking : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "Origin", table: "CharacterWeapons", type: "TEXT", nullable: false, defaultValue: "Legacy");
        migrationBuilder.AddColumn<int>(name: "SpentFragments", table: "CharacterWeaponSkills", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<bool>(name: "StarterWeaponRewardClaimed", table: "Users", type: "INTEGER", nullable: false, defaultValue: false);
        // Historical costs were 2/4/8. Never revalue old investment using a new price curve.
        migrationBuilder.Sql("UPDATE CharacterWeaponSkills SET SpentFragments = CASE EnhancementLevel WHEN 1 THEN 2 WHEN 2 THEN 6 WHEN 3 THEN 14 ELSE 0 END;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("StarterWeaponRewardClaimed", "Users");
        migrationBuilder.DropColumn("SpentFragments", "CharacterWeaponSkills");
        migrationBuilder.DropColumn("Origin", "CharacterWeapons");
    }
}
