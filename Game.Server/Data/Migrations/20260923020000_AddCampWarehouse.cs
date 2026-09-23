using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260923020000_AddCampWarehouse")]
public sealed class AddCampWarehouse : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "UserWarehouseStacks",
            columns: table => new
            {
                UserId = table.Column<int>(type: "INTEGER", nullable: false),
                ItemCode = table.Column<string>(type: "TEXT", nullable: false),
                Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                Version = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserWarehouseStacks", row => new { row.UserId, row.ItemCode });
                table.CheckConstraint("CK_UserWarehouseStacks_Quantity", "Quantity >= 0");
            });

        migrationBuilder.CreateTable(
            name: "WarehouseTransferRecords",
            columns: table => new
            {
                UserId = table.Column<int>(type: "INTEGER", nullable: false),
                RequestId = table.Column<string>(type: "TEXT", nullable: false),
                CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                ItemCode = table.Column<string>(type: "TEXT", nullable: false),
                Direction = table.Column<string>(type: "TEXT", nullable: false),
                Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_WarehouseTransferRecords", row => new { row.UserId, row.RequestId }));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("WarehouseTransferRecords");
        migrationBuilder.DropTable("UserWarehouseStacks");
    }
}
