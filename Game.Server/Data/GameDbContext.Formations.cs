using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Data;

public partial class GameDbContext
{
    public DbSet<CharacterBattleFormation> CharacterBattleFormations => Set<CharacterBattleFormation>();
    public DbSet<FormationWeaponSlot> FormationWeaponSlots => Set<FormationWeaponSlot>();
    public DbSet<FormationSkillSlot> FormationSkillSlots => Set<FormationSkillSlot>();
    public DbSet<FormationConsumableSlot> FormationConsumableSlots => Set<FormationConsumableSlot>();
    public DbSet<CharacterFormationState> CharacterFormationStates => Set<CharacterFormationState>();
    public DbSet<CharacterBattleFormationPreference> CharacterBattleFormationPreferences => Set<CharacterBattleFormationPreference>();
    public DbSet<BattleAdmissionReceipt> BattleAdmissionReceipts => Set<BattleAdmissionReceipt>();

    private static void ConfigureFormations(ModelBuilder model)
    {
        var formation = model.Entity<CharacterBattleFormation>();
        formation.Property(x => x.Version).IsConcurrencyToken();
        formation.HasOne<Character>().WithMany().HasForeignKey(x => x.CharacterId).OnDelete(DeleteBehavior.Cascade);
        formation.HasIndex(x => new { x.CharacterId, x.GroupElement, x.Position }).IsUnique().HasFilter("IsDeleted = 0");
        formation.HasIndex(x => x.SoulImprintId);
        formation.ToTable(t => t.HasCheckConstraint("CK_Formations_Position", "Position BETWEEN 1 AND 20 AND GroupElement BETWEEN 0 AND 5"));
        model.Entity<FormationWeaponSlot>().HasKey(x => new { x.FormationId, x.SlotIndex });
        formation.HasMany(x => x.Weapons).WithOne().HasForeignKey(x => x.FormationId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<FormationWeaponSlot>().HasIndex(x => x.WeaponId);
        model.Entity<FormationWeaponSlot>().HasIndex(x => new { x.FormationId, x.WeaponId }).IsUnique().HasFilter("WeaponId IS NOT NULL");
        model.Entity<FormationWeaponSlot>().ToTable(t => t.HasCheckConstraint("CK_FormationWeapons_Slot", "SlotIndex BETWEEN 1 AND 10"));
        model.Entity<FormationSkillSlot>().HasKey(x => new { x.FormationId, x.SlotIndex });
        formation.HasMany(x => x.Skills).WithOne().HasForeignKey(x => x.FormationId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<FormationSkillSlot>().ToTable(t => t.HasCheckConstraint("CK_FormationSkills_Slot", "SlotIndex BETWEEN 1 AND 5 AND AutoHpThresholdPercent BETWEEN 1 AND 100"));
        model.Entity<FormationConsumableSlot>().HasKey(x => new { x.FormationId, x.SlotIndex });
        formation.HasMany(x => x.Consumables).WithOne().HasForeignKey(x => x.FormationId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<FormationConsumableSlot>().ToTable(t => t.HasCheckConstraint("CK_FormationConsumables_Slot", "SlotIndex BETWEEN 1 AND 3 AND AutoHpThresholdPercent BETWEEN 1 AND 100"));
        model.Entity<CharacterFormationState>().HasKey(x => x.CharacterId);
        model.Entity<CharacterFormationState>().Property(x => x.CharacterId).ValueGeneratedNever();
        model.Entity<CharacterFormationState>().HasOne<Character>().WithMany().HasForeignKey(x => x.CharacterId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<CharacterFormationState>().Property(x => x.Version).IsConcurrencyToken();
        model.Entity<CharacterBattleFormationPreference>().HasKey(x => new { x.CharacterId, x.DungeonCode, x.DepthLevel });
        model.Entity<CharacterBattleFormationPreference>().HasOne<Character>().WithMany().HasForeignKey(x => x.CharacterId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<CharacterBattleFormationPreference>().Property(x => x.Version).IsConcurrencyToken();
        model.Entity<CharacterBattleFormationPreference>().ToTable(t => t.HasCheckConstraint("CK_FormationPreference_Depth", "DepthLevel >= 1"));
        model.Entity<BattleAdmissionReceipt>().HasKey(x => new { x.UserId, x.RequestId });
        model.Entity<BattleAdmissionReceipt>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<RoomOperation>().HasIndex(x => new { x.UserId, x.RequestId }).IsUnique().HasFilter("RequestId IS NOT NULL");
    }
}
