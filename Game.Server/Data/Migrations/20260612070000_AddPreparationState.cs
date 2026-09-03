using Game.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260612070000_AddPreparationState")]
public partial class AddPreparationState : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsConfirmed",
            table: "RoomSlots",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "IsConfirmed",
            table: "RoomSlots");
    }
}
