using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260927100000_SpecializeCombatConsumables")]
public sealed class SpecializeCombatConsumables : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Test-environment cutover: no old loadout or in-progress-run compatibility.
        // Inventory, production, gardens and earned rewards are deliberately untouched.
        migrationBuilder.Sql("""
            UPDATE Characters SET Hp = MIN(Hp, MAX(1, CAST(MaxHp *
                (1.0 + CAST(WeaponHealthBonusPercent AS REAL) / 100.0) *
                (1.0 + CAST(TalentMaxHpPercent AS REAL) / 100.0) AS INTEGER))), Version = Version + 1
            WHERE Id IN (SELECT CharacterId FROM RoomSlots WHERE CharacterId IS NOT NULL);
            UPDATE Rooms SET Status = 2, ClosedAtUtc = COALESCE(ClosedAtUtc, CURRENT_TIMESTAMP),
                BattleEndedAtUtc = COALESCE(BattleEndedAtUtc, CURRENT_TIMESTAMP), IsRepeatBattle = 0,
                IsOwnerAutoEnabled = 0, NextRoundAvailableAtUtc = NULL, RoundCooldownDurationSeconds = NULL,
                PreparationStartedAtUtc = NULL, Version = Version + 1;
            UPDATE RoomOperations SET Status = 'Cancelled', Error = 'ConsumableRulesChanged',
                FinishedAtUtc = CURRENT_TIMESTAMP, Version = Version + 1 WHERE Status = 'Pending';
            UPDATE RoomSlots SET CharacterId = NULL, UserId = NULL, LastSeenAtUtc = NULL,
                IsConfirmed = 0, IsAutoEnabled = 0, IsTemporaryAuto = 0, PendingConsumableSlotIndex = NULL,
                PendingSkillSlotMask = 0, PendingSkillTargetsJson = NULL, IsSoulImprintQueued = 0,
                HasParticipatedInRun = 0, LastParticipatedMonsterId = NULL;
            DELETE FROM CharacterActivities WHERE Kind = 'Battle';
            DELETE FROM CharacterConsumableSlots;
            DELETE FROM BattleConsumableCooldowns;
            DELETE FROM BattleConsumableBuffs;
            DELETE FROM BattleOperationPotionStates;
            """);
        migrationBuilder.Sql("ALTER TABLE RoomSlots DROP COLUMN PendingConsumableSlotIndex;");
        migrationBuilder.AddColumn<int>("PendingConsumableSlotMask", "RoomSlots", "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.CreateTable("BattleHealingPotionStates", columns: table => new
        {
            RoomId = table.Column<int>("INTEGER", nullable: false),
            RunSequence = table.Column<int>("INTEGER", nullable: false),
            CharacterId = table.Column<int>("INTEGER", nullable: false),
            UsesUsed = table.Column<int>("INTEGER", nullable: false),
            Version = table.Column<int>("INTEGER", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_BattleHealingPotionStates", row => new { row.RoomId, row.RunSequence, row.CharacterId });
            table.CheckConstraint("CK_BattleHealingPotionStates_Uses", "UsesUsed BETWEEN 0 AND 2");
        });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("BattleHealingPotionStates");
        migrationBuilder.Sql("ALTER TABLE RoomSlots DROP COLUMN PendingConsumableSlotMask;");
        migrationBuilder.AddColumn<int>("PendingConsumableSlotIndex", "RoomSlots", "INTEGER", nullable: true);
        // Schema rollback cannot restore intentionally discarded test loadouts or runs.
    }
}
