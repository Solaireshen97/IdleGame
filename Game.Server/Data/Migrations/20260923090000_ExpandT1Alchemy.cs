using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260923090000_ExpandT1Alchemy")]
public sealed class ExpandT1Alchemy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "BattleConsumableBuffs",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                RoomId = table.Column<int>(type: "INTEGER", nullable: false),
                RunSequence = table.Column<int>(type: "INTEGER", nullable: false),
                CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                ItemCode = table.Column<string>(type: "TEXT", nullable: false),
                WeaponSkillCode = table.Column<string>(type: "TEXT", nullable: false),
                SkillLevel = table.Column<int>(type: "INTEGER", nullable: false),
                AppliedRound = table.Column<int>(type: "INTEGER", nullable: false),
                ExpiresAfterRound = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BattleConsumableBuffs", row => row.Id);
                table.CheckConstraint("CK_BattleConsumableBuffs_Values",
                    "SkillLevel > 0 AND ExpiresAfterRound >= AppliedRound");
            });
        migrationBuilder.CreateIndex(
            name: "IX_BattleConsumableBuffs_RoomId_RunSequence_CharacterId_WeaponSkillCode_AppliedRound",
            table: "BattleConsumableBuffs",
            columns: ["RoomId", "RunSequence", "CharacterId", "WeaponSkillCode", "AppliedRound"],
            unique: true);
        foreach (var column in new[]
                 {
                     "FinalDamagePercent", "DamageTakenPercent",
                     "NormalAttackDamagePercent", "AreaDamageReductionPercent"
                 })
            migrationBuilder.AddColumn<int>(name: column, table: "BattleOperationPotionStates",
                type: "INTEGER", nullable: false, defaultValue: 0);

        // The old second Elwynn rare point is retired. Keep earned opportunities
        // by moving them to the region's single active rare gathering point.
        migrationBuilder.Sql("""
            UPDATE CharacterGatheringOpportunities
            SET AvailableCount = AvailableCount + COALESCE((
                    SELECT legacy.AvailableCount FROM CharacterGatheringOpportunities legacy
                    WHERE legacy.CharacterId = CharacterGatheringOpportunities.CharacterId
                      AND legacy.PointCode = 'elwynn-briarthorn'), 0),
                EarnedCount = EarnedCount + COALESCE((
                    SELECT legacy.EarnedCount FROM CharacterGatheringOpportunities legacy
                    WHERE legacy.CharacterId = CharacterGatheringOpportunities.CharacterId
                      AND legacy.PointCode = 'elwynn-briarthorn'), 0),
                SpentCount = SpentCount + COALESCE((
                    SELECT legacy.SpentCount FROM CharacterGatheringOpportunities legacy
                    WHERE legacy.CharacterId = CharacterGatheringOpportunities.CharacterId
                      AND legacy.PointCode = 'elwynn-briarthorn'), 0)
            WHERE PointCode = 'elwynn-earthroot'
              AND EXISTS (SELECT 1 FROM CharacterGatheringOpportunities legacy
                          WHERE legacy.CharacterId = CharacterGatheringOpportunities.CharacterId
                            AND legacy.PointCode = 'elwynn-briarthorn');
            """);
        migrationBuilder.Sql("""
            UPDATE CharacterGatheringOpportunities
            SET PointCode = 'elwynn-earthroot'
            WHERE PointCode = 'elwynn-briarthorn'
              AND CharacterId NOT IN (SELECT CharacterId FROM CharacterGatheringOpportunities
                                      WHERE PointCode = 'elwynn-earthroot');
            """);
        migrationBuilder.Sql("DELETE FROM CharacterGatheringOpportunities WHERE PointCode = 'elwynn-briarthorn';");
        migrationBuilder.Sql("""
            UPDATE GatheringTasks
            SET PointCode = 'elwynn-earthroot', MaterialCode = 'earthroot'
            WHERE PointCode = 'elwynn-briarthorn' AND Status = 'Running';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("BattleConsumableBuffs");
        foreach (var column in new[]
                 {
                     "FinalDamagePercent", "DamageTakenPercent",
                     "NormalAttackDamagePercent", "AreaDamageReductionPercent"
                 })
            migrationBuilder.DropColumn(name: column, table: "BattleOperationPotionStates");
    }
}
