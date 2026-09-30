using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260930010000_AddBattleStatusOwnership")]
public sealed class AddBattleStatusOwnership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("SourceActorType", "BattleStatusEffects", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<int>("SourceActorId", "BattleStatusEffects", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<string>("SourceSkillCode", "BattleStatusEffects", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>("BoundTargetType", "BattleStatusEffects", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<int>("BoundTargetId", "BattleStatusEffects", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<int>("Lifetime", "BattleStatusEffects", type: "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<decimal>("MagnitudeSnapshot", "BattleStatusEffects", type: "TEXT", nullable: true);
        migrationBuilder.Sql("""
            UPDATE BattleStatusEffects
            SET BoundTargetType = 'Monster', BoundTargetId = PerTickValue, PerTickValue = NULL,
                SourceActorType = 'Character', SourceActorId = TargetId
            WHERE TargetType = 'Character' AND EffectCode IN ('mage-disorder', 'hunter-prey-mark')
                AND PerTickValue IS NOT NULL;
            UPDATE BattleStatusEffects SET Lifetime = 2 WHERE EffectCode = 'mage-disorder';
            UPDATE BattleStatusEffects SET Lifetime = 1
            WHERE EffectCode IN ('rogue-shadow-charge', 'acolyte-next-heal', 'acolyte-next-damage', 'acolyte-revelation')
                OR EffectCode LIKE 'mage-skill-disruption-%';
            UPDATE BattleStatusEffects SET SourceActorType = 'Character', SourceActorId = TargetId
            WHERE TargetType = 'Character' AND EffectCode IN
                ('rogue-shadow-charge', 'acolyte-next-heal', 'acolyte-next-damage', 'acolyte-revelation');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE BattleStatusEffects SET PerTickValue = BoundTargetId
            WHERE EffectCode IN ('mage-disorder', 'hunter-prey-mark') AND BoundTargetType = 'Monster';
            """);
        // These hand-written historical migrations have no intermediate target models.
        // Native SQLite column removal also preserves the existing table and its indexes.
        migrationBuilder.Sql("""
            ALTER TABLE BattleStatusEffects DROP COLUMN SourceActorType;
            ALTER TABLE BattleStatusEffects DROP COLUMN SourceActorId;
            ALTER TABLE BattleStatusEffects DROP COLUMN SourceSkillCode;
            ALTER TABLE BattleStatusEffects DROP COLUMN BoundTargetType;
            ALTER TABLE BattleStatusEffects DROP COLUMN BoundTargetId;
            ALTER TABLE BattleStatusEffects DROP COLUMN Lifetime;
            ALTER TABLE BattleStatusEffects DROP COLUMN MagnitudeSnapshot;
            """);
    }
}
