using Game.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260920010000_MovePreparationTimeoutToRoomCreation")]
public partial class MovePreparationTimeoutToRoomCreation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameColumn(
            name: "IsSelfTeamPreparationTimeoutEnabled",
            table: "Rooms",
            newName: "IsPreparationTimeoutEnabled");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameColumn(
            name: "IsPreparationTimeoutEnabled",
            table: "Rooms",
            newName: "IsSelfTeamPreparationTimeoutEnabled");
    }
}
