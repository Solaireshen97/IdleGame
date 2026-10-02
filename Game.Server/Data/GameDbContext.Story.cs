using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Data;

public partial class GameDbContext
{
    public DbSet<UserStoryState> UserStoryStates => Set<UserStoryState>();
    public DbSet<StoryQuestProgress> StoryQuestProgress => Set<StoryQuestProgress>();
    public DbSet<StoryMapUnlock> StoryMapUnlocks => Set<StoryMapUnlock>();
    public DbSet<StoryActionReceipt> StoryActionReceipts => Set<StoryActionReceipt>();
    public DbSet<StoryEventReceipt> StoryEventReceipts => Set<StoryEventReceipt>();

    private static void ConfigureStory(ModelBuilder model)
    {
        var state = model.Entity<UserStoryState>();
        state.ToTable("UserStoryStates");
        state.HasKey(x => x.UserId); state.Property(x => x.UserId).ValueGeneratedNever();
        state.Property(x => x.Version).IsConcurrencyToken();
        state.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        var quest = model.Entity<StoryQuestProgress>();
        quest.ToTable("StoryQuestProgress", t => t.HasCheckConstraint("CK_StoryQuestProgress_Progress", "Progress >= 0"));
        quest.HasKey(x => new { x.UserId, x.QuestCode });
        quest.Property(x => x.Version).IsConcurrencyToken();
        quest.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        var map = model.Entity<StoryMapUnlock>();
        map.ToTable("StoryMapUnlocks"); map.HasKey(x => new { x.UserId, x.MapNodeCode });
        map.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        var receipt = model.Entity<StoryActionReceipt>();
        receipt.ToTable("StoryActionReceipts"); receipt.HasKey(x => new { x.UserId, x.RequestId });
        receipt.HasIndex(x => new { x.UserId, x.QuestCode }).IsUnique().HasFilter("QuestCode IS NOT NULL");
        receipt.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        var evt = model.Entity<StoryEventReceipt>();
        evt.ToTable("StoryEventReceipts"); evt.HasKey(x => new { x.UserId, x.QuestCode, x.SourceEventId });
        evt.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
