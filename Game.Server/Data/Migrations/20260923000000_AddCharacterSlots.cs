using Game.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260923000000_AddCharacterSlots")]
public sealed class AddCharacterSlots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "CharacterSlotLimit",
            table: "Users",
            type: "INTEGER",
            nullable: false,
            defaultValue: 2);

        // Preserve slots already in use by older accounts, up to the new hard cap.
        migrationBuilder.Sql("""
            UPDATE Users
            SET CharacterSlotLimit = CASE
                WHEN (SELECT COUNT(*) FROM Characters WHERE Characters.UserId = Users.Id) >= 5 THEN 5
                WHEN (SELECT COUNT(*) FROM Characters WHERE Characters.UserId = Users.Id) > 2
                    THEN (SELECT COUNT(*) FROM Characters WHERE Characters.UserId = Users.Id)
                ELSE 2
            END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "CharacterSlotLimit", table: "Users");
    }
}
