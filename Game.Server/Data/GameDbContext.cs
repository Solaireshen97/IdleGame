using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Data;

public class GameDbContext(DbContextOptions<GameDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Character> Characters => Set<Character>();
    public DbSet<CharacterActivity> CharacterActivities => Set<CharacterActivity>();
    public DbSet<CharacterBattleMilestone> CharacterBattleMilestones => Set<CharacterBattleMilestone>();
    public DbSet<CharacterGatheringOpportunity> CharacterGatheringOpportunities => Set<CharacterGatheringOpportunity>();
    public DbSet<CharacterProfessionTalent> CharacterProfessionTalents => Set<CharacterProfessionTalent>();
    public DbSet<GatheringTask> GatheringTasks => Set<GatheringTask>();
    public DbSet<ProductionTask> ProductionTasks => Set<ProductionTask>();
    public DbSet<Monster> Monsters => Set<Monster>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<RoomSlot> RoomSlots => Set<RoomSlot>();
    public DbSet<UserLoginSession> UserLoginSessions => Set<UserLoginSession>();
    public DbSet<Dungeon> Dungeons => Set<Dungeon>();
    public DbSet<UserDungeonClear> UserDungeonClears => Set<UserDungeonClear>();
    public DbSet<CharacterItemStack> CharacterItemStacks => Set<CharacterItemStack>();
    public DbSet<CharacterConsumableSlot> CharacterConsumableSlots => Set<CharacterConsumableSlot>();
    public DbSet<BattleConsumableCooldown> BattleConsumableCooldowns => Set<BattleConsumableCooldown>();
    public DbSet<BattleConsumableBuff> BattleConsumableBuffs => Set<BattleConsumableBuff>();
    public DbSet<BattleOperationPotionState> BattleOperationPotionStates => Set<BattleOperationPotionState>();
    public DbSet<CharacterSkillSlot> CharacterSkillSlots => Set<CharacterSkillSlot>();
    public DbSet<BattleSkillCooldown> BattleSkillCooldowns => Set<BattleSkillCooldown>();
    public DbSet<CharacterSkillTalent> CharacterSkillTalents => Set<CharacterSkillTalent>();
    public DbSet<CharacterWeapon> CharacterWeapons => Set<CharacterWeapon>();
    public DbSet<CharacterWeaponSkill> CharacterWeaponSkills => Set<CharacterWeaponSkill>();
    public DbSet<RewardRun> RewardRuns => Set<RewardRun>();
    public DbSet<RewardEvent> RewardEvents => Set<RewardEvent>();
    public DbSet<RewardEntry> RewardEntries => Set<RewardEntry>();
    public DbSet<MonsterIntent> MonsterIntents => Set<MonsterIntent>();
    public DbSet<BattleStatusEffect> BattleStatusEffects => Set<BattleStatusEffect>();
    public DbSet<BattleMonsterSkillCooldown> BattleMonsterSkillCooldowns => Set<BattleMonsterSkillCooldown>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CharacterActivity>().HasKey(activity => activity.CharacterId);
        modelBuilder.Entity<CharacterActivity>().Property(activity => activity.CharacterId).ValueGeneratedNever();
        modelBuilder.Entity<CharacterActivity>().HasIndex(activity => new { activity.Kind, activity.SourceId });
        modelBuilder.Entity<CharacterBattleMilestone>().HasKey(milestone => new { milestone.CharacterId, milestone.Kind, milestone.TargetCode });
        modelBuilder.Entity<CharacterBattleMilestone>().ToTable(table => table.HasCheckConstraint("CK_CharacterBattleMilestones_Count", "Count > 0"));
        modelBuilder.Entity<CharacterGatheringOpportunity>().HasKey(item => new { item.CharacterId, item.PointCode });
        modelBuilder.Entity<CharacterProfessionTalent>().HasKey(item => new { item.CharacterId, item.ProfessionCode, item.NodeCode });
        modelBuilder.Entity<CharacterProfessionTalent>().ToTable(table => table.HasCheckConstraint(
            "CK_CharacterProfessionTalents_Rank", "Rank > 0"));
        modelBuilder.Entity<CharacterGatheringOpportunity>().Property(item => item.Version).IsConcurrencyToken();
        modelBuilder.Entity<CharacterGatheringOpportunity>().ToTable(table => table.HasCheckConstraint(
            "CK_CharacterGatheringOpportunities_Counts",
            "AvailableCount >= 0 AND EarnedCount >= 0 AND SpentCount >= 0 AND EarnedCount = AvailableCount + SpentCount"));
        modelBuilder.Entity<GatheringTask>().Property(task => task.Version).IsConcurrencyToken();
        modelBuilder.Entity<GatheringTask>().HasIndex(task => new { task.CharacterId, task.Status });
        modelBuilder.Entity<GatheringTask>().ToTable(table => table.HasCheckConstraint("CK_GatheringTasks_Quantities", "CompletedCycles >= 0 AND TotalQuantity >= 0 AND CycleSeconds > 0 AND OutputQuantity > 0"));
        modelBuilder.Entity<ProductionTask>().Property(task => task.Version).IsConcurrencyToken();
        modelBuilder.Entity<ProductionTask>().HasIndex(task => new { task.CharacterId, task.Status });
        modelBuilder.Entity<ProductionTask>().ToTable(table => table.HasCheckConstraint("CK_ProductionTasks_Quantities",
            "CompletedCycles >= 0 AND TotalQuantity >= 0 AND CycleSeconds > 0 AND OutputQuantity > 0"));
        modelBuilder.Entity<User>().Property(user => user.Version).IsConcurrencyToken();
        modelBuilder.Entity<RewardRun>().HasKey(run => new { run.RoomId, run.Sequence });
        modelBuilder.Entity<RewardEvent>().HasKey(entry => new { entry.RoomId, entry.Sequence, entry.EventKey });
        modelBuilder.Entity<RewardEntry>().HasIndex(entry => new { entry.RoomId, entry.Sequence, entry.UserId });
        modelBuilder.Entity<RewardEntry>().ToTable(table => table.HasCheckConstraint("CK_RewardEntries_Quantity", "Quantity > 0"));
        modelBuilder.Entity<Character>()
            .Property(character => character.Version)
            .IsConcurrencyToken();
        modelBuilder.Entity<Character>().Property(character => character.GatheringLevel).HasDefaultValue(1);
        modelBuilder.Entity<Character>().Property(character => character.AlchemyLevel).HasDefaultValue(1);
        modelBuilder.Entity<CharacterItemStack>()
            .Property(stack => stack.Version)
            .IsConcurrencyToken();
        modelBuilder.Entity<CharacterItemStack>()
            .HasIndex(stack => new { stack.CharacterId, stack.ItemCode })
            .IsUnique();
        modelBuilder.Entity<CharacterItemStack>()
            .ToTable(table => table.HasCheckConstraint("CK_CharacterItemStacks_Quantity", "Quantity >= 0"));
        modelBuilder.Entity<CharacterConsumableSlot>()
            .Property(slot => slot.Version)
            .IsConcurrencyToken();
        modelBuilder.Entity<CharacterConsumableSlot>()
            .HasIndex(slot => new { slot.CharacterId, slot.SlotIndex })
            .IsUnique();
        modelBuilder.Entity<CharacterConsumableSlot>()
            .ToTable(table => table.HasCheckConstraint("CK_CharacterConsumableSlots_Threshold", "AutoHpThresholdPercent BETWEEN 1 AND 100"));
        modelBuilder.Entity<BattleConsumableCooldown>()
            .HasIndex(cooldown => new { cooldown.RoomId, cooldown.CharacterId, cooldown.CooldownGroup })
            .IsUnique();
        modelBuilder.Entity<BattleConsumableBuff>()
            .HasIndex(buff => new { buff.RoomId, buff.RunSequence, buff.CharacterId, buff.WeaponSkillCode, buff.AppliedRound })
            .IsUnique();
        modelBuilder.Entity<BattleConsumableBuff>()
            .ToTable(table => table.HasCheckConstraint("CK_BattleConsumableBuffs_Values",
                "SkillLevel > 0 AND ExpiresAfterRound >= AppliedRound"));
        modelBuilder.Entity<BattleOperationPotionState>()
            .HasKey(state => new { state.RoomId, state.RunSequence, state.CharacterId });
        modelBuilder.Entity<BattleOperationPotionState>()
            .ToTable(table => table.HasCheckConstraint("CK_BattleOperationPotionStates_AttackPercent",
                "AttackPercent BETWEEN 0 AND 100"));
        modelBuilder.Entity<CharacterSkillSlot>()
            .Property(slot => slot.Version)
            .IsConcurrencyToken();
        modelBuilder.Entity<CharacterSkillSlot>()
            .HasIndex(slot => new { slot.CharacterId, slot.SlotIndex })
            .IsUnique();
        modelBuilder.Entity<CharacterSkillSlot>()
            .ToTable(table => table.HasCheckConstraint("CK_CharacterSkillSlots_Threshold", "AutoHpThresholdPercent BETWEEN 1 AND 100"));
        modelBuilder.Entity<BattleSkillCooldown>()
            .HasIndex(cooldown => new { cooldown.RoomId, cooldown.CharacterId, cooldown.SkillCode })
            .IsUnique();
        modelBuilder.Entity<CharacterSkillTalent>()
            .HasIndex(talent => new { talent.CharacterId, talent.NodeCode })
            .IsUnique();
        modelBuilder.Entity<CharacterSkillTalent>()
            .ToTable(table => table.HasCheckConstraint("CK_CharacterSkillTalents_PointsSpent", "PointsSpent > 0"));
        modelBuilder.Entity<CharacterWeapon>()
            .Property(weapon => weapon.Element)
            .HasConversion<string>();
        modelBuilder.Entity<Dungeon>()
            .Property(dungeon => dungeon.MonsterElement)
            .HasConversion<string>();
        modelBuilder.Entity<Dungeon>()
            .ToTable(table => table.HasCheckConstraint("CK_Dungeons_Levels",
                "MinimumLevel > 0 AND RecommendedLevel >= MinimumLevel"));
        modelBuilder.Entity<Monster>()
            .Property(monster => monster.Element)
            .HasConversion<string>();
        modelBuilder.Entity<Monster>()
            .HasIndex(monster => new { monster.RoomId, monster.WaveNumber, monster.Position })
            .IsUnique()
            .HasFilter("RoomId IS NOT NULL");
        modelBuilder.Entity<MonsterIntent>()
            .HasIndex(intent => new { intent.RoomId, intent.RunSequence, intent.RoundNumber, intent.MonsterId })
            .IsUnique();
        modelBuilder.Entity<BattleStatusEffect>()
            .HasIndex(effect => new { effect.RoomId, effect.RunSequence, effect.TargetType, effect.TargetId, effect.EffectCode })
            .IsUnique();
        modelBuilder.Entity<BattleStatusEffect>()
            .ToTable(table => table.HasCheckConstraint("CK_BattleStatusEffects_Values",
                "Stacks > 0 AND ExpiresAfterRound >= AppliedRound"));
        modelBuilder.Entity<BattleMonsterSkillCooldown>()
            .HasIndex(cooldown => new { cooldown.RoomId, cooldown.MonsterId, cooldown.SkillCode })
            .IsUnique();
        modelBuilder.Entity<CharacterWeapon>()
            .Property(weapon => weapon.Origin).HasConversion<string>();
        modelBuilder.Entity<CharacterWeapon>()
            .Property(weapon => weapon.Version)
            .IsConcurrencyToken();
        modelBuilder.Entity<CharacterWeapon>()
            .HasIndex(weapon => new { weapon.CharacterId, weapon.EquippedSlotIndex })
            .IsUnique()
            .HasFilter("EquippedSlotIndex IS NOT NULL");
        modelBuilder.Entity<CharacterWeapon>()
            .ToTable(table =>
            {
                table.HasCheckConstraint("CK_CharacterWeapons_Stats", "Attack >= 0 AND MaxHp > 0");
                table.HasCheckConstraint("CK_CharacterWeapons_Progression", "ItemLevel > 0");
                table.HasCheckConstraint("CK_CharacterWeapons_Recycling", "SellGold >= 0 AND DismantleFragments > 0");
                table.HasCheckConstraint("CK_CharacterWeapons_Slot", "EquippedSlotIndex IS NULL OR EquippedSlotIndex BETWEEN 1 AND 10");
            });
        modelBuilder.Entity<CharacterWeaponSkill>()
            .HasOne<CharacterWeapon>()
            .WithMany(weapon => weapon.Skills)
            .HasForeignKey(skill => skill.WeaponId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CharacterWeaponSkill>()
            .HasIndex(skill => new { skill.WeaponId, skill.SlotIndex })
            .IsUnique();
        modelBuilder.Entity<CharacterWeaponSkill>()
            .HasIndex(skill => new { skill.WeaponId, skill.SkillCode })
            .IsUnique();
        modelBuilder.Entity<CharacterWeaponSkill>()
            .ToTable(table =>
            {
                table.HasCheckConstraint("CK_CharacterWeaponSkills_Level", "Level BETWEEN 1 AND 20");
                table.HasCheckConstraint("CK_CharacterWeaponSkills_Progression", "BaseLevel BETWEEN 1 AND 20 AND QualityBonusLevel = 0 AND EnhancementLevel BETWEEN 0 AND 6 AND Level = BaseLevel + EnhancementLevel");
                table.HasCheckConstraint("CK_CharacterWeaponSkills_Slot", "SlotIndex BETWEEN 1 AND 3");
            });

        modelBuilder.Entity<Room>()
            .Property(room => room.Version)
            .IsConcurrencyToken();

        modelBuilder.Entity<RoomSlot>()
            .HasIndex(slot => new { slot.RoomId, slot.SlotIndex })
            .IsUnique();
        modelBuilder.Entity<RoomSlot>()
            .HasIndex(slot => slot.CharacterId)
            .IsUnique();
        modelBuilder.Entity<RoomSlot>()
            .HasIndex(slot => slot.RoomId);
        modelBuilder.Entity<Dungeon>()
            .HasIndex(dungeon => dungeon.Code)
            .IsUnique();
        modelBuilder.Entity<UserDungeonClear>()
            .HasIndex(clear => new { clear.UserId, clear.DungeonId })
            .IsUnique();
    }
}
