using System;
using Game.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations
{
    [DbContext(typeof(GameDbContext))]
    [Migration("20260612050000_AddRoundStateAndConcurrency")]
    public class AddRoundStateAndConcurrency : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BattleEndedAtUtc",
                table: "Rooms",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextRoundAvailableAtUtc",
                table: "Rooms",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Rooms",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("UPDATE Rooms SET Status = 0 WHERE Status = 1;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "BattleEndedAtUtc", table: "Rooms");
            migrationBuilder.DropColumn(name: "NextRoundAvailableAtUtc", table: "Rooms");
            migrationBuilder.DropColumn(name: "Version", table: "Rooms");
        }
    }
}
