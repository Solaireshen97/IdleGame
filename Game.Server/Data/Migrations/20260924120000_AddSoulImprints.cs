using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260924120000_AddSoulImprints")]
public sealed class AddSoulImprints : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsSoulImprintQueued",
            table: "RoomSlots",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateTable(
            name: "CharacterSoulImprints",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                SoulImprintCode = table.Column<string>(type: "TEXT", nullable: false),
                EquippedSlotIndex = table.Column<int>(type: "INTEGER", nullable: true),
                AutoUseEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                IsLocked = table.Column<bool>(type: "INTEGER", nullable: false),
                AcquiredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                Version = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CharacterSoulImprints", x => x.Id);
                table.CheckConstraint("CK_CharacterSoulImprints_Slot",
                    "EquippedSlotIndex IS NULL OR EquippedSlotIndex = 1");
            });

        migrationBuilder.CreateIndex(
            name: "IX_CharacterSoulImprints_CharacterId_EquippedSlotIndex",
            table: "CharacterSoulImprints",
            columns: new[] { "CharacterId", "EquippedSlotIndex" },
            unique: true,
            filter: "EquippedSlotIndex IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_CharacterSoulImprints_CharacterId_SoulImprintCode",
            table: "CharacterSoulImprints",
            columns: new[] { "CharacterId", "SoulImprintCode" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "CharacterSoulImprints");
        migrationBuilder.DropColumn(name: "IsSoulImprintQueued", table: "RoomSlots");
    }
}
