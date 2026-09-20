using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Data;

public class GameDbContext(DbContextOptions<GameDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Character> Characters => Set<Character>();
    public DbSet<Monster> Monsters => Set<Monster>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<RoomSlot> RoomSlots => Set<RoomSlot>();
    public DbSet<UserLoginSession> UserLoginSessions => Set<UserLoginSession>();
    public DbSet<Dungeon> Dungeons => Set<Dungeon>();
    public DbSet<UserDungeonClear> UserDungeonClears => Set<UserDungeonClear>();
    public DbSet<CharacterItemStack> CharacterItemStacks => Set<CharacterItemStack>();
    public DbSet<CharacterConsumableSlot> CharacterConsumableSlots => Set<CharacterConsumableSlot>();
    public DbSet<BattleConsumableCooldown> BattleConsumableCooldowns => Set<BattleConsumableCooldown>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Character>()
            .Property(character => character.Version)
            .IsConcurrencyToken();
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
