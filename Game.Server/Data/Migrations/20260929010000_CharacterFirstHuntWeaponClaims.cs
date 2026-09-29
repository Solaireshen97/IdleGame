using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260929010000_CharacterFirstHuntWeaponClaims")]
public sealed class CharacterFirstHuntWeaponClaims : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("CharacterFirstHuntWeaponClaims", columns: table => new
        {
            CharacterId = table.Column<int>("INTEGER", nullable: false),
            DungeonId = table.Column<int>("INTEGER", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_CharacterFirstHuntWeaponClaims", x => new { x.CharacterId, x.DungeonId }));
        migrationBuilder.Sql("ALTER TABLE Users DROP COLUMN StarterWeaponRewardClaimed;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>("StarterWeaponRewardClaimed", "Users", "INTEGER", nullable: false, defaultValue: false);
        migrationBuilder.DropTable("CharacterFirstHuntWeaponClaims");
    }
}
