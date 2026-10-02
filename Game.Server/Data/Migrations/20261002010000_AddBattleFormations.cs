using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20261002010000_AddBattleFormations")]
public sealed class AddBattleFormations : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        migration.Sql("""
            CREATE TABLE CharacterBattleFormations (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, CharacterId INTEGER NOT NULL,
                GroupElement INTEGER NOT NULL, Position INTEGER NOT NULL, Name TEXT NOT NULL,
                ProfessionCode TEXT NOT NULL, SoulImprintId INTEGER NULL, SoulAutoUseEnabled INTEGER NOT NULL,
                Version INTEGER NOT NULL, IsDeleted INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL,
                CONSTRAINT FK_Formations_Characters FOREIGN KEY(CharacterId) REFERENCES Characters(Id) ON DELETE CASCADE,
                CONSTRAINT CK_Formations_Position CHECK(Position BETWEEN 1 AND 20 AND GroupElement BETWEEN 0 AND 5));
            CREATE UNIQUE INDEX IX_CharacterBattleFormations_CharacterId_GroupElement_Position ON CharacterBattleFormations(CharacterId,GroupElement,Position) WHERE IsDeleted = 0;
            CREATE INDEX IX_CharacterBattleFormations_SoulImprintId ON CharacterBattleFormations(SoulImprintId);
            CREATE TABLE FormationWeaponSlots (
                FormationId INTEGER NOT NULL, SlotIndex INTEGER NOT NULL, WeaponId INTEGER NULL,
                PRIMARY KEY(FormationId,SlotIndex), FOREIGN KEY(FormationId) REFERENCES CharacterBattleFormations(Id) ON DELETE CASCADE,
                CONSTRAINT CK_FormationWeapons_Slot CHECK(SlotIndex BETWEEN 1 AND 10));
            CREATE INDEX IX_FormationWeaponSlots_WeaponId ON FormationWeaponSlots(WeaponId);
            CREATE UNIQUE INDEX IX_FormationWeaponSlots_FormationId_WeaponId ON FormationWeaponSlots(FormationId,WeaponId) WHERE WeaponId IS NOT NULL;
            CREATE TABLE FormationSkillSlots (
                FormationId INTEGER NOT NULL, SlotIndex INTEGER NOT NULL, SkillCode TEXT NULL,
                AutoUseEnabled INTEGER NOT NULL, AutoConditionOverride TEXT NULL, AutoHpThresholdPercent INTEGER NOT NULL,
                PRIMARY KEY(FormationId,SlotIndex), FOREIGN KEY(FormationId) REFERENCES CharacterBattleFormations(Id) ON DELETE CASCADE,
                CONSTRAINT CK_FormationSkills_Slot CHECK(SlotIndex BETWEEN 1 AND 5 AND AutoHpThresholdPercent BETWEEN 1 AND 100));
            CREATE TABLE FormationConsumableSlots (
                FormationId INTEGER NOT NULL, SlotIndex INTEGER NOT NULL, ItemCode TEXT NULL,
                AutoUseEnabled INTEGER NOT NULL, AutoHpThresholdPercent INTEGER NOT NULL,
                PRIMARY KEY(FormationId,SlotIndex), FOREIGN KEY(FormationId) REFERENCES CharacterBattleFormations(Id) ON DELETE CASCADE,
                CONSTRAINT CK_FormationConsumables_Slot CHECK(SlotIndex BETWEEN 1 AND 3 AND AutoHpThresholdPercent BETWEEN 1 AND 100));
            CREATE TABLE CharacterFormationStates (
                CharacterId INTEGER NOT NULL PRIMARY KEY, DefaultFormationId INTEGER NULL, AppliedFormationId INTEGER NULL,
                AppliedFormationVersion INTEGER NULL, AppliedChoiceHash TEXT NULL, Version INTEGER NOT NULL,
                FOREIGN KEY(CharacterId) REFERENCES Characters(Id) ON DELETE CASCADE);
            CREATE TABLE CharacterBattleFormationPreferences (
                CharacterId INTEGER NOT NULL, DungeonCode TEXT NOT NULL, DepthLevel INTEGER NOT NULL, FormationId INTEGER NOT NULL,
                Version INTEGER NOT NULL, LastUsedAtUtc TEXT NOT NULL, PRIMARY KEY(CharacterId,DungeonCode,DepthLevel),
                FOREIGN KEY(CharacterId) REFERENCES Characters(Id) ON DELETE CASCADE,
                CONSTRAINT CK_FormationPreference_Depth CHECK(DepthLevel >= 1));
            CREATE TABLE BattleAdmissionReceipts (
                UserId INTEGER NOT NULL, RequestId TEXT NOT NULL, Action TEXT NOT NULL, Fingerprint TEXT NOT NULL,
                RoomId INTEGER NULL, CharacterId INTEGER NOT NULL, ResultJson TEXT NULL, CreatedAtUtc TEXT NOT NULL,
                PRIMARY KEY(UserId,RequestId), FOREIGN KEY(UserId) REFERENCES Users(Id) ON DELETE CASCADE);
            """);
        migration.AddColumn<string>("LoadoutIntegrityError", "Rooms", type: "TEXT", nullable: true);
        foreach (var table in new[] { "RoomSlots", "RoomOperations" })
        {
            migration.AddColumn<int>("SourceFormationId", table, type: "INTEGER", nullable: true);
            migration.AddColumn<int>("SourceFormationVersion", table, type: "INTEGER", nullable: true);
            migration.AddColumn<string>("SourceFormationName", table, type: "TEXT", nullable: true);
        }
        migration.AddColumn<string>("AppliedLoadoutJson", "RoomSlots", type: "TEXT", nullable: true);
        migration.AddColumn<string>("AutoPolicyOverridesJson", "RoomSlots", type: "TEXT", nullable: true);
        migration.AddColumn<string>("RequestedLoadoutJson", "RoomOperations", type: "TEXT", nullable: true);
        migration.AddColumn<string>("RequestId", "RoomOperations", type: "TEXT", nullable: true);
        migration.AddColumn<string>("RequestFingerprint", "RoomOperations", type: "TEXT", nullable: true);
        migration.CreateIndex("IX_RoomOperations_UserId_RequestId", "RoomOperations", new[] { "UserId", "RequestId" }, unique: true, filter: "RequestId IS NOT NULL");
        migration.AddColumn<bool>("RememberForEncounter", "RoomOperations", type: "INTEGER", nullable: false, defaultValue: false);
    }
    protected override void Down(MigrationBuilder migration)
    {
        foreach (var table in new[] { "FormationWeaponSlots", "FormationSkillSlots", "FormationConsumableSlots", "CharacterFormationStates", "CharacterBattleFormationPreferences", "BattleAdmissionReceipts", "CharacterBattleFormations" }) migration.DropTable(table);
        // Handwritten historical migrations have no target model for EF's table rebuild.
        // SQLite's native DROP COLUMN supports these unconstrained nullable columns.
        migration.Sql("ALTER TABLE Rooms DROP COLUMN LoadoutIntegrityError;");
        foreach (var table in new[] { "RoomSlots", "RoomOperations" })
            foreach (var column in new[] { "SourceFormationId", "SourceFormationVersion", "SourceFormationName" }) migration.Sql($"ALTER TABLE {table} DROP COLUMN {column};");
        foreach (var column in new[] { "AppliedLoadoutJson", "AutoPolicyOverridesJson" }) migration.Sql($"ALTER TABLE RoomSlots DROP COLUMN {column};");
        migration.DropIndex("IX_RoomOperations_UserId_RequestId", "RoomOperations");
        foreach (var column in new[] { "RequestedLoadoutJson", "RequestId", "RequestFingerprint", "RememberForEncounter" }) migration.Sql($"ALTER TABLE RoomOperations DROP COLUMN {column};");
    }
}
