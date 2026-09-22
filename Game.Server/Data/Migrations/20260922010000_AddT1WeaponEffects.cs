using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260922010000_AddT1WeaponEffects")]
public sealed class AddT1WeaponEffects : Migration
{
    private static readonly string[] Columns = ["WeaponStaminaPercent", "WeaponEnmityPercent",
        "WeaponDoubleAttackChancePercent", "WeaponNormalEchoPercent", "WeaponSkillDamagePercent"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var name in Columns)
            migrationBuilder.AddColumn<decimal>(name, "Characters", type: "TEXT", nullable: false, defaultValue: 0m);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var name in Columns) migrationBuilder.DropColumn(name, "Characters");
    }
}
