using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260927000000_AddPartyHpScaling")]
public sealed class AddPartyHpScaling : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "PartyScalingProfileCode", table: "Dungeons",
            type: "TEXT", nullable: false, defaultValue: "fixed");
        migrationBuilder.AddColumn<int>(name: "BaseMaxHp", table: "Monsters",
            type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>(name: "ScalingPartySize", table: "Rooms",
            type: "INTEGER", nullable: false, defaultValue: 1);
        migrationBuilder.Sql("UPDATE Monsters SET BaseMaxHp = MaxHp;");
        migrationBuilder.Sql("UPDATE Dungeons SET PartyScalingProfileCode = 'hunt-hp' WHERE DungeonKind IN ('Hunt', 'Elite');");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Restore single-character health before removing the information needed to undo scaling.
        migrationBuilder.Sql("UPDATE Monsters SET Hp = CASE WHEN Hp <= 0 THEN 0 ELSE MIN(BaseMaxHp, CAST((CAST(Hp AS INTEGER) * BaseMaxHp + MaxHp - 1) / MaxHp AS INTEGER)) END, MaxHp = BaseMaxHp WHERE BaseMaxHp > 0 AND MaxHp > 0;");
        migrationBuilder.Sql("ALTER TABLE Rooms DROP COLUMN ScalingPartySize;");
        migrationBuilder.Sql("ALTER TABLE Monsters DROP COLUMN BaseMaxHp;");
        migrationBuilder.Sql("ALTER TABLE Dungeons DROP COLUMN PartyScalingProfileCode;");
    }
}
