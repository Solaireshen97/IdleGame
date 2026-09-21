using Game.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260921010000_UnifyTalentTree")]
public partial class UnifyTalentTree : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The former skill-only tree had no attribute prerequisites. Refund its purchases
        // so existing characters can choose paths in the unified attribute tree.
        migrationBuilder.Sql("""
            UPDATE Characters
            SET TalentPoints = TalentPoints + (
                SELECT COALESCE(SUM(PointsSpent), 0)
                FROM CharacterSkillTalents
                WHERE CharacterId = Characters.Id
            ), Version = Version + 1
            WHERE Id IN (SELECT CharacterId FROM CharacterSkillTalents);
            """);
        migrationBuilder.Sql("""
            UPDATE CharacterSkillSlots
            SET SkillCode = NULL, AutoUseEnabled = 0, Version = Version + 1
            WHERE SkillCode IN (
                'knight-break', 'knight-wall', 'knight-assault', 'knight-verdict',
                'cleric-blessing', 'cleric-sanctuary', 'cleric-mercy', 'cleric-judgment'
            );
            """);
        migrationBuilder.Sql("""
            UPDATE RoomSlots SET PendingSkillSlotMask = 0
            WHERE CharacterId IN (SELECT CharacterId FROM CharacterSkillTalents);
            """);
        migrationBuilder.Sql("""
            UPDATE Rooms SET Version = Version + 1
            WHERE Id IN (SELECT RoomId FROM RoomSlots
                         WHERE CharacterId IN (SELECT CharacterId FROM CharacterSkillTalents));
            """);
        migrationBuilder.Sql("""
            DELETE FROM BattleSkillCooldowns
            WHERE CharacterId IN (SELECT CharacterId FROM CharacterSkillTalents);
            """);
        migrationBuilder.Sql("DELETE FROM CharacterSkillTalents;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Refunded player choices cannot be reconstructed after they are spent again.
    }
}
