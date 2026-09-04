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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
