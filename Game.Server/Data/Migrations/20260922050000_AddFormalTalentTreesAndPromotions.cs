using Game.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260922050000_AddFormalTalentTreesAndPromotions")]
public sealed class AddFormalTalentTreesAndPromotions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "AdvancedProfessionCode", table: "Characters", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<decimal>(name: "TalentHealingDonePercent", table: "Characters", type: "TEXT", nullable: false, defaultValue: 0m);
        migrationBuilder.AddColumn<decimal>(name: "TalentHealingReceivedPercent", table: "Characters", type: "TEXT", nullable: false, defaultValue: 0m);
        migrationBuilder.AddColumn<decimal>(name: "TalentMaxHpPercent", table: "Characters", type: "TEXT", nullable: false, defaultValue: 0m);
        migrationBuilder.AddColumn<decimal>(name: "TalentNormalAttackPercent", table: "Characters", type: "TEXT", nullable: false, defaultValue: 0m);
        migrationBuilder.AddColumn<decimal>(name: "TalentSkillCriticalChancePercent", table: "Characters", type: "TEXT", nullable: false, defaultValue: 0m);
        migrationBuilder.AddColumn<decimal>(name: "TalentSkillDamagePercent", table: "Characters", type: "TEXT", nullable: false, defaultValue: 0m);

        migrationBuilder.Sql("""
            UPDATE Characters
            SET ProfessionCode = CASE WHEN ProfessionCode = 'cleric' THEN 'acolyte' ELSE 'swordsman' END,
                TalentPoints = CASE WHEN Level > 1 THEN Level - 1 ELSE 0 END,
                AttackTalentRank = 0,
                HealthTalentRank = 0;
            DELETE FROM CharacterSkillTalents;
            UPDATE CharacterSkillSlots
            SET SkillCode = CASE SkillCode
                WHEN 'knight-strike' THEN 'sword-slash'
                WHEN 'knight-guard' THEN 'sword-parry'
                WHEN 'cleric-smite' THEN 'acolyte-holy-bolt'
                WHEN 'cleric-heal' THEN 'acolyte-heal'
                ELSE NULL END,
                AutoUseEnabled = 0;
            DELETE FROM BattleSkillCooldowns;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE Characters SET ProfessionCode = CASE WHEN ProfessionCode = 'acolyte' THEN 'cleric' ELSE 'knight' END;
            """);
        migrationBuilder.DropColumn(name: "AdvancedProfessionCode", table: "Characters");
        migrationBuilder.DropColumn(name: "TalentHealingDonePercent", table: "Characters");
        migrationBuilder.DropColumn(name: "TalentHealingReceivedPercent", table: "Characters");
        migrationBuilder.DropColumn(name: "TalentMaxHpPercent", table: "Characters");
        migrationBuilder.DropColumn(name: "TalentNormalAttackPercent", table: "Characters");
        migrationBuilder.DropColumn(name: "TalentSkillCriticalChancePercent", table: "Characters");
        migrationBuilder.DropColumn(name: "TalentSkillDamagePercent", table: "Characters");
    }
}
