using Game.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260920050000_AddCombatConsumables")]
public partial class AddCombatConsumables : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "RoundNumber", table: "Rooms", type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>(name: "PendingConsumableSlotIndex", table: "RoomSlots", type: "INTEGER", nullable: true);

        migrationBuilder.CreateTable(
            name: "CharacterItemStacks",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                ItemCode = table.Column<string>(type: "TEXT", nullable: false),
                Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                Version = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CharacterItemStacks", x => x.Id);
                table.CheckConstraint("CK_CharacterItemStacks_Quantity", "Quantity >= 0");
            });

        migrationBuilder.CreateTable(
            name: "CharacterConsumableSlots",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                SlotIndex = table.Column<int>(type: "INTEGER", nullable: false),
                ItemCode = table.Column<string>(type: "TEXT", nullable: true),
                AutoUseEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                AutoHpThresholdPercent = table.Column<int>(type: "INTEGER", nullable: false),
                Version = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CharacterConsumableSlots", x => x.Id);
                table.CheckConstraint("CK_CharacterConsumableSlots_Threshold", "AutoHpThresholdPercent BETWEEN 1 AND 100");
            });

        migrationBuilder.CreateTable(
            name: "BattleConsumableCooldowns",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                RoomId = table.Column<int>(type: "INTEGER", nullable: false),
                CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                CooldownGroup = table.Column<string>(type: "TEXT", nullable: false),
                ReadyAtRound = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_BattleConsumableCooldowns", x => x.Id));

        migrationBuilder.CreateIndex(name: "IX_CharacterItemStacks_CharacterId_ItemCode", table: "CharacterItemStacks", columns: new[] { "CharacterId", "ItemCode" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_CharacterConsumableSlots_CharacterId_SlotIndex", table: "CharacterConsumableSlots", columns: new[] { "CharacterId", "SlotIndex" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_BattleConsumableCooldowns_RoomId_CharacterId_CooldownGroup", table: "BattleConsumableCooldowns", columns: new[] { "RoomId", "CharacterId", "CooldownGroup" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "BattleConsumableCooldowns");
        migrationBuilder.DropTable(name: "CharacterConsumableSlots");
        migrationBuilder.DropTable(name: "CharacterItemStacks");
        migrationBuilder.DropColumn(name: "PendingConsumableSlotIndex", table: "RoomSlots");
        migrationBuilder.DropColumn(name: "RoundNumber", table: "Rooms");
    }
}
