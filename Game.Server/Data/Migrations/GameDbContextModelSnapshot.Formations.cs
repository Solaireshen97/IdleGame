using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Game.Server.Data.Migrations;

partial class GameDbContextModelSnapshot
{
    private static void AddFormations(ModelBuilder model)
    {
        // Match the repository's manually maintained snapshot without discovering CLR navigations.
        static EntityTypeBuilder Scalars(ModelBuilder model, Type type, string table)
        {
            var b = model.Entity(type.FullName!);
            // A model snapshot must freeze its property contract rather than reflect future CLR additions.
            var fields = table switch
            {
                "CharacterBattleFormations" => new (string, Type)[]
                {
                    ("Id", typeof(int)), ("CharacterId", typeof(int)), ("GroupElement", typeof(Game.Shared.Enums.ElementType)),
                    ("Position", typeof(int)), ("Name", typeof(string)), ("ProfessionCode", typeof(string)),
                    ("SoulImprintId", typeof(int?)), ("SoulAutoUseEnabled", typeof(bool)), ("Version", typeof(int)),
                    ("SoulAutoConditionOverride", typeof(string)), ("SoulAutoHpThresholdPercent", typeof(int)),
                    ("IsDeleted", typeof(bool)), ("CreatedAtUtc", typeof(DateTime)), ("UpdatedAtUtc", typeof(DateTime))
                },
                "FormationWeaponSlots" => [("FormationId", typeof(int)), ("SlotIndex", typeof(int)), ("WeaponId", typeof(int?))],
                "FormationSkillSlots" => [("FormationId", typeof(int)), ("SlotIndex", typeof(int)), ("SkillCode", typeof(string)),
                    ("AutoUseEnabled", typeof(bool)), ("AutoConditionOverride", typeof(string)), ("AutoHpThresholdPercent", typeof(int))],
                "FormationConsumableSlots" => [("FormationId", typeof(int)), ("SlotIndex", typeof(int)), ("ItemCode", typeof(string)),
                    ("AutoUseEnabled", typeof(bool)), ("AutoConditionOverride", typeof(string)), ("AutoHpThresholdPercent", typeof(int))],
                "CharacterFormationStates" => [("CharacterId", typeof(int)), ("DefaultFormationId", typeof(int?)),
                    ("AppliedFormationId", typeof(int?)), ("AppliedFormationVersion", typeof(int?)), ("AppliedChoiceHash", typeof(string)), ("Version", typeof(int))],
                "CharacterBattleFormationPreferences" => [("CharacterId", typeof(int)), ("DungeonCode", typeof(string)), ("DepthLevel", typeof(int)),
                    ("FormationId", typeof(int)), ("Version", typeof(int)), ("LastUsedAtUtc", typeof(DateTime))],
                "BattleAdmissionReceipts" => [("UserId", typeof(int)), ("RequestId", typeof(string)), ("Action", typeof(string)),
                    ("Fingerprint", typeof(string)), ("RoomId", typeof(int?)), ("CharacterId", typeof(int)), ("ResultJson", typeof(string)), ("CreatedAtUtc", typeof(DateTime))],
                _ => throw new InvalidOperationException(table)
            };
            foreach (var (name, fieldType) in fields)
            {
                var property = b.Property(fieldType, name);
                property.HasColumnType(fieldType == typeof(string) || fieldType == typeof(DateTime) ? "TEXT" : "INTEGER");
                if (fieldType == typeof(string) && name is "Name" or "ProfessionCode" or "DungeonCode" or "RequestId" or "Action" or "Fingerprint") property.IsRequired();
            }
            b.ToTable(table); return b;
        }
        var formation = Scalars(model, typeof(CharacterBattleFormation), "CharacterBattleFormations");
        formation.Property<int>("Id").ValueGeneratedOnAdd(); formation.HasKey("Id");
        formation.Property<int>("Version").IsConcurrencyToken();
        formation.HasIndex("CharacterId", "GroupElement", "Position").IsUnique().HasFilter("IsDeleted = 0");
        formation.HasIndex("SoulImprintId");
        formation.ToTable("CharacterBattleFormations", t => t.HasCheckConstraint("CK_Formations_Position", "Position BETWEEN 1 AND 20 AND GroupElement BETWEEN 0 AND 5"));
        formation.HasOne(typeof(Character).FullName!, null).WithMany().HasForeignKey("CharacterId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        var weapon = Scalars(model, typeof(FormationWeaponSlot), "FormationWeaponSlots");
        weapon.HasKey("FormationId", "SlotIndex"); weapon.HasIndex("WeaponId");
        weapon.HasIndex("FormationId", "WeaponId").IsUnique().HasFilter("WeaponId IS NOT NULL");
        weapon.ToTable("FormationWeaponSlots", t => t.HasCheckConstraint("CK_FormationWeapons_Slot", "SlotIndex BETWEEN 1 AND 10"));
        weapon.HasOne(typeof(CharacterBattleFormation).FullName!, null).WithMany("Weapons").HasForeignKey("FormationId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        var skill = Scalars(model, typeof(FormationSkillSlot), "FormationSkillSlots");
        skill.HasKey("FormationId", "SlotIndex");
        skill.ToTable("FormationSkillSlots", t => t.HasCheckConstraint("CK_FormationSkills_Slot", "SlotIndex BETWEEN 1 AND 5 AND AutoHpThresholdPercent BETWEEN 1 AND 100"));
        skill.HasOne(typeof(CharacterBattleFormation).FullName!, null).WithMany("Skills").HasForeignKey("FormationId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        var potion = Scalars(model, typeof(FormationConsumableSlot), "FormationConsumableSlots");
        potion.HasKey("FormationId", "SlotIndex");
        potion.ToTable("FormationConsumableSlots", t => t.HasCheckConstraint("CK_FormationConsumables_Slot", "SlotIndex BETWEEN 1 AND 3 AND AutoHpThresholdPercent BETWEEN 1 AND 100"));
        potion.HasOne(typeof(CharacterBattleFormation).FullName!, null).WithMany("Consumables").HasForeignKey("FormationId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        var state = Scalars(model, typeof(CharacterFormationState), "CharacterFormationStates");
        state.HasKey("CharacterId"); state.Property<int>("CharacterId").ValueGeneratedNever(); state.Property<int>("Version").IsConcurrencyToken();
        state.HasOne(typeof(Character).FullName!, null).WithMany().HasForeignKey("CharacterId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        var preference = Scalars(model, typeof(CharacterBattleFormationPreference), "CharacterBattleFormationPreferences");
        preference.HasKey("CharacterId", "DungeonCode", "DepthLevel"); preference.Property<int>("Version").IsConcurrencyToken();
        preference.ToTable("CharacterBattleFormationPreferences", t => t.HasCheckConstraint("CK_FormationPreference_Depth", "DepthLevel >= 1"));
        preference.HasOne(typeof(Character).FullName!, null).WithMany().HasForeignKey("CharacterId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        var receipt = Scalars(model, typeof(BattleAdmissionReceipt), "BattleAdmissionReceipts");
        receipt.HasKey("UserId", "RequestId"); receipt.HasOne(typeof(User).FullName!, null).WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        foreach (var type in new[] { typeof(RoomSlot), typeof(RoomOperation) })
        {
            var b = model.Entity(type.FullName!);
            b.Property<int?>("SourceFormationId").HasColumnType("INTEGER");
            b.Property<int?>("SourceFormationVersion").HasColumnType("INTEGER");
            b.Property<string>("SourceFormationName").HasColumnType("TEXT");
        }
        model.Entity(typeof(Room).FullName!).Property<string>("LoadoutIntegrityError").HasColumnType("TEXT");
        model.Entity(typeof(RoomSlot).FullName!).Property<string>("AppliedLoadoutJson").HasColumnType("TEXT");
        model.Entity(typeof(RoomSlot).FullName!).Property<string>("AutoPolicyOverridesJson").HasColumnType("TEXT");
        model.Entity(typeof(RoomOperation).FullName!).Property<string>("RequestedLoadoutJson").HasColumnType("TEXT");
        model.Entity(typeof(RoomOperation).FullName!).Property<string>("RequestId").HasColumnType("TEXT");
        model.Entity(typeof(RoomOperation).FullName!).Property<string>("RequestFingerprint").HasColumnType("TEXT");
        model.Entity(typeof(RoomOperation).FullName!).HasIndex("UserId", "RequestId").IsUnique().HasFilter("RequestId IS NOT NULL");
        model.Entity(typeof(RoomOperation).FullName!).Property<bool>("RememberForEncounter").HasColumnType("INTEGER");
    }
}
