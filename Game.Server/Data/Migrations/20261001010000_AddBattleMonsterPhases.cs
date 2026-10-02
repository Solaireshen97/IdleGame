using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20261001010000_AddBattleMonsterPhases")]
public sealed class AddBattleMonsterPhases : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "BattleMonsterPhaseStates",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                RoomId = table.Column<int>(type: "INTEGER", nullable: false),
                RunSequence = table.Column<int>(type: "INTEGER", nullable: false),
                MonsterId = table.Column<int>(type: "INTEGER", nullable: false),
                EncounterStartRound = table.Column<int>(type: "INTEGER", nullable: false),
                LastPreparedRound = table.Column<int>(type: "INTEGER", nullable: false),
                NextActivationRound = table.Column<int>(type: "INTEGER", nullable: false),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                ExpiresAfterRound = table.Column<int>(type: "INTEGER", nullable: false),
                WaterDamage = table.Column<long>(type: "INTEGER", nullable: false),
                RewardStartsAtRound = table.Column<int>(type: "INTEGER", nullable: true),
                LastActivationRound = table.Column<int>(type: "INTEGER", nullable: true),
                LastBreakRound = table.Column<int>(type: "INTEGER", nullable: true),
                LastExpiryRound = table.Column<int>(type: "INTEGER", nullable: true),
                ActivationCount = table.Column<int>(type: "INTEGER", nullable: false),
                BreakCount = table.Column<int>(type: "INTEGER", nullable: false),
                ExpiryCount = table.Column<int>(type: "INTEGER", nullable: false),
                LinkedHitCount = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BattleMonsterPhaseStates", row => row.Id);
                table.ForeignKey("FK_BattleMonsterPhaseStates_Rooms_RoomId", row => row.RoomId,
                    "Rooms", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex("IX_BattleMonsterPhaseStates_RoomId_RunSequence_MonsterId",
            "BattleMonsterPhaseStates", new[] { "RoomId", "RunSequence", "MonsterId" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("BattleMonsterPhaseStates");
}
