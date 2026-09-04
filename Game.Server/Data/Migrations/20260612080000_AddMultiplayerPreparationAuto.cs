using Game.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260612080000_AddMultiplayerPreparationAuto")]
public partial class AddMultiplayerPreparationAuto : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "PreparationStartedAtUtc",
            table: "Rooms",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "IsAutoEnabled",
            table: "RoomSlots",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "IsTemporaryAuto",
            table: "RoomSlots",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PreparationStartedAtUtc", table: "Rooms");
        migrationBuilder.DropColumn(name: "IsAutoEnabled", table: "RoomSlots");
        migrationBuilder.DropColumn(name: "IsTemporaryAuto", table: "RoomSlots");
    }
}