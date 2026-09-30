using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260930030000_AddAccountSessionIndexes")]
public sealed class AddAccountSessionIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Fail without altering accounts when a legacy database contains duplicates.
        // SQLite RAISE requires a trigger; this temporary guard leaves no permanent schema.
        migrationBuilder.Sql("""
            CREATE TEMP TABLE AccountSessionIndexGuard (Value INTEGER);
            CREATE TEMP TRIGGER CheckAccountSessionIndexDuplicates BEFORE INSERT ON AccountSessionIndexGuard
            BEGIN
                SELECT CASE WHEN EXISTS (SELECT 1 FROM Users GROUP BY UserName HAVING COUNT(*) > 1)
                    THEN RAISE(ABORT, 'Account/session migration blocked: duplicate Users.UserName values. Resolve duplicate accounts explicitly before retrying; no accounts were deleted.') END;
                SELECT CASE WHEN EXISTS (SELECT 1 FROM UserLoginSessions GROUP BY Token HAVING COUNT(*) > 1)
                    THEN RAISE(ABORT, 'Account/session migration blocked: duplicate UserLoginSessions.Token values. Resolve duplicate sessions explicitly before retrying; no sessions were deleted.') END;
            END;
            INSERT INTO AccountSessionIndexGuard VALUES (1);
            DROP TABLE AccountSessionIndexGuard;
            """);
        migrationBuilder.CreateIndex("IX_Users_UserName", "Users", "UserName", unique: true);
        migrationBuilder.CreateIndex("IX_UserLoginSessions_Token", "UserLoginSessions", "Token", unique: true);
        migrationBuilder.CreateIndex("IX_UserLoginSessions_ExpireAt", "UserLoginSessions", "ExpireAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_Users_UserName", "Users");
        migrationBuilder.DropIndex("IX_UserLoginSessions_Token", "UserLoginSessions");
        migrationBuilder.DropIndex("IX_UserLoginSessions_ExpireAt", "UserLoginSessions");
    }
}
