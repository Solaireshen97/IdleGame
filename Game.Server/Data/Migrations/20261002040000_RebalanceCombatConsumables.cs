using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20261002040000_RebalanceCombatConsumables")]
public sealed class RebalanceCombatConsumables : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("AutoConditionOverride", "CharacterConsumableSlots", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>("AutoConditionOverride", "FormationConsumableSlots", type: "TEXT", nullable: true);
        migrationBuilder.Sql("""
            ALTER TABLE BattleHealingPotionStates ADD COLUMN BuffUsesUsed INTEGER NOT NULL DEFAULT 0
                CONSTRAINT CK_BattleHealingPotionStates_BuffUses CHECK (BuffUsesUsed BETWEEN 0 AND 2);
            INSERT INTO BattleHealingPotionStates (RoomId, RunSequence, CharacterId, UsesUsed, BuffUsesUsed, Version)
                SELECT RoomId, RunSequence, CharacterId, 0, MIN(2, COUNT(*)), 0
                FROM BattleConsumableBuffs WHERE 1 = 1 GROUP BY RoomId, RunSequence, CharacterId
                ON CONFLICT (RoomId, RunSequence, CharacterId) DO UPDATE SET
                    BuffUsesUsed = excluded.BuffUsesUsed, Version = BattleHealingPotionStates.Version + 1;
            """);

        // Finish the current cycle of retired recipes; an infinite old task must not keep producing them.
        migrationBuilder.Sql("""
            UPDATE ProductionTasks SET TargetCycles = CompletedCycles + 1, Version = Version + 1
            WHERE Status = 'Running' AND RecipeCode IN ('travel-healing-potion-elwynn', 'elwynn-assault-legacy-batch');
            """);
        foreach (var (oldCode, newCode) in new[]
        {
            ("travel-healing-potion", "minor-healing-potion"),
            ("lesser-travel-healing-potion", "lesser-minor-healing-potion")
        })
        {
            migrationBuilder.Sql($"""
                UPDATE CharacterFormationStates SET AppliedChoiceHash = NULL, Version = Version + 1
                WHERE CharacterId IN (SELECT CharacterId FROM CharacterConsumableSlots WHERE ItemCode = '{oldCode}');
                INSERT INTO CharacterItemStacks (CharacterId, ItemCode, Quantity, Version)
                    SELECT CharacterId, '{newCode}', Quantity, Version + 1 FROM CharacterItemStacks WHERE ItemCode = '{oldCode}'
                    ON CONFLICT (CharacterId, ItemCode) DO UPDATE SET
                        Quantity = CharacterItemStacks.Quantity + excluded.Quantity, Version = CharacterItemStacks.Version + 1;
                DELETE FROM CharacterItemStacks WHERE ItemCode = '{oldCode}';
                UPDATE CharacterConsumableSlots SET ItemCode = '{newCode}', Version = Version + 1 WHERE ItemCode = '{oldCode}';
                UPDATE FormationConsumableSlots SET ItemCode = '{newCode}' WHERE ItemCode = '{oldCode}';
                UPDATE ProductionTasks SET OutputCode = '{newCode}', Version = Version + 1 WHERE OutputCode = '{oldCode}';
                UPDATE RewardEntries SET Code = '{newCode}' WHERE Kind = 'Consumable' AND Code = '{oldCode}';
                UPDATE RoomSlots SET AppliedLoadoutJson = replace(AppliedLoadoutJson, '"{oldCode}"', '"{newCode}"')
                    WHERE AppliedLoadoutJson IS NOT NULL;
                UPDATE RoomOperations SET RequestedLoadoutJson = replace(RequestedLoadoutJson, '"{oldCode}"', '"{newCode}"')
                    WHERE RequestedLoadoutJson IS NOT NULL;
                UPDATE BattleAdmissionReceipts SET ResultJson = replace(ResultJson, '"{oldCode}"', '"{newCode}"')
                    WHERE ResultJson IS NOT NULL;
                """);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Merged inventory stays merged: reversing it would invent or lose bottles.
        migrationBuilder.Sql("ALTER TABLE FormationConsumableSlots DROP COLUMN AutoConditionOverride;");
        migrationBuilder.Sql("ALTER TABLE CharacterConsumableSlots DROP COLUMN AutoConditionOverride;");
        migrationBuilder.Sql("ALTER TABLE BattleHealingPotionStates DROP COLUMN BuffUsesUsed;");
    }
}
