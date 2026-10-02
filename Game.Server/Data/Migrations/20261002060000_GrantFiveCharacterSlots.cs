using Game.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20261002060000_GrantFiveCharacterSlots")]
public sealed class GrantFiveCharacterSlots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        UPDATE Users SET CharacterSlotLimit = 5, Version = Version + 1
        WHERE CharacterSlotLimit < 5;
        """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Free slots are an account entitlement. A rollback must not revoke them
        // or invalidate characters created after the grant.
    }
}
