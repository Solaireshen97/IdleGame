using Game.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260920060000_AddStarterSkills")]
public partial class AddStarterSkills : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "ProfessionCode", table: "Characters", type: "TEXT", nullable: false, defaultValue: "knight");
        migrationBuilder.AddColumn<int>(name: "PendingSkillSlotMask", table: "RoomSlots", type: "INTEGER", nullable: false, defaultValue: 0);

        migrationBuilder.CreateTable(
            name: "CharacterSkillSlots",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                SlotIndex = table.Column<int>(type: "INTEGER", nullable: false),
                SkillCode = table.Column<string>(type: "TEXT", nullable: true),
                AutoUseEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                AutoHpThresholdPercent = table.Column<int>(type: "INTEGER", nullable: false),
                Version = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CharacterSkillSlots", x => x.Id);
                table.CheckConstraint("CK_CharacterSkillSlots_Threshold", "AutoHpThresholdPercent BETWEEN 1 AND 100");
            });

        migrationBuilder.CreateTable(
            name: "BattleSkillCooldowns",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                RoomId = table.Column<int>(type: "INTEGER", nullable: false),
                CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                SkillCode = table.Column<string>(type: "TEXT", nullable: false),
                ReadyAtRound = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_BattleSkillCooldowns", x => x.Id));

        migrationBuilder.CreateIndex(name: "IX_CharacterSkillSlots_CharacterId_SlotIndex", table: "CharacterSkillSlots", columns: new[] { "CharacterId", "SlotIndex" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_BattleSkillCooldowns_RoomId_CharacterId_SkillCode", table: "BattleSkillCooldowns", columns: new[] { "RoomId", "CharacterId", "SkillCode" }, unique: true);

        migrationBuilder.Sql("INSERT INTO CharacterSkillSlots (CharacterId, SlotIndex, SkillCode, AutoUseEnabled, AutoHpThresholdPercent, Version) SELECT Id, 1, 'knight-strike', 0, 70, 0 FROM Characters");
        migrationBuilder.Sql("INSERT INTO CharacterSkillSlots (CharacterId, SlotIndex, SkillCode, AutoUseEnabled, AutoHpThresholdPercent, Version) SELECT Id, 2, 'knight-guard', 0, 70, 0 FROM Characters");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "BattleSkillCooldowns");
        migrationBuilder.DropTable(name: "CharacterSkillSlots");
        migrationBuilder.DropColumn(name: "PendingSkillSlotMask", table: "RoomSlots");
        migrationBuilder.DropColumn(name: "ProfessionCode", table: "Characters");
    }
}
