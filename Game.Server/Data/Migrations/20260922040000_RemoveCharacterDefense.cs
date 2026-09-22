using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260922040000_RemoveCharacterDefense")]
public sealed class RemoveCharacterDefense : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Return every point spent in the retired defense talent before removing it.
        migrationBuilder.Sql("UPDATE Characters SET TalentPoints = TalentPoints + (DefenseTalentRank * (DefenseTalentRank + 1) / 2);");
        migrationBuilder.Sql("ALTER TABLE Characters DROP COLUMN DefenseTalentRank;");
        migrationBuilder.Sql("ALTER TABLE Characters DROP COLUMN Defense;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE Characters ADD COLUMN Defense INTEGER NOT NULL DEFAULT 0;");
        migrationBuilder.Sql("ALTER TABLE Characters ADD COLUMN DefenseTalentRank INTEGER NOT NULL DEFAULT 0;");
    }
}
