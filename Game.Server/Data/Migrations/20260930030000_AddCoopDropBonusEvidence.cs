using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260930030000_AddCoopDropBonusEvidence")]
public sealed class AddCoopDropBonusEvidence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("CoopParticipantCount", "RewardEvents", type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<decimal>("CoopDropBonusPercent", "RewardEvents", type: "TEXT", nullable: false, defaultValue: 0m);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Historical hand-written migrations lack intermediate target models for EF's table rebuild.
        migrationBuilder.Sql("""
            ALTER TABLE RewardEvents DROP COLUMN CoopParticipantCount;
            ALTER TABLE RewardEvents DROP COLUMN CoopDropBonusPercent;
            """);
    }
}
