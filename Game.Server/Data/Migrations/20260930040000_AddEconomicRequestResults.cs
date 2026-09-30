using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260930040000_AddEconomicRequestResults")]
public sealed class AddEconomicRequestResults : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>("ResultJson", "LogisticsRequests", type: "TEXT", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // A result snapshot is part of a committed command's identity. Dropping it
        // while keeping the receipt would make future retries unrecoverable.
        migrationBuilder.Sql("""
            CREATE TEMP TABLE EconomicRequestResultRollbackGuard (Value INTEGER);
            CREATE TEMP TRIGGER CheckEconomicRequestResultRollback BEFORE INSERT ON EconomicRequestResultRollbackGuard
            BEGIN
                SELECT CASE WHEN EXISTS (SELECT 1 FROM LogisticsRequests WHERE ResultJson IS NOT NULL)
                    THEN RAISE(ABORT, 'Economic request migration rollback blocked: stored receipt results would be lost. Restore the pre-release database backup instead; no receipts were deleted.') END;
            END;
            INSERT INTO EconomicRequestResultRollbackGuard VALUES (1);
            DROP TABLE EconomicRequestResultRollbackGuard;
            ALTER TABLE LogisticsRequests DROP COLUMN ResultJson;
            """);
    }
}
