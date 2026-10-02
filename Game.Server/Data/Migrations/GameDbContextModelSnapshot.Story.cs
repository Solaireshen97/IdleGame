using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Game.Server.Data.Migrations;

partial class GameDbContextModelSnapshot
{
    private static void AddStory(ModelBuilder model)
    {
        // Explicit string-based contracts keep this snapshot independent of future CLR model changes.
        static EntityTypeBuilder Entity(ModelBuilder model, string name, string table,
            (string Name, Type Type, bool Required)[] fields)
        {
            var b = model.Entity("Game.Shared.Models." + name);
            foreach (var (propertyName, type, required) in fields)
            {
                var p = b.Property(type, propertyName)
                    .HasColumnType(type == typeof(string) || type == typeof(DateTime) || type == typeof(DateTime?) ? "TEXT" : "INTEGER");
                if (required) p.IsRequired();
            }
            b.ToTable(table);
            b.HasOne("Game.Shared.Models.User", null).WithMany().HasForeignKey("UserId")
                .OnDelete(DeleteBehavior.Cascade).IsRequired();
            return b;
        }
        var state = Entity(model, "UserStoryState", "UserStoryStates", [
            ("UserId", typeof(int), true), ("IsLegacy", typeof(bool), true),
            ("TutorialCharacterId", typeof(int?), false), ("CurrentQuestCode", typeof(string), false),
            ("ChapterCode", typeof(string), true), ("ChapterCompleted", typeof(bool), true),
            ("StoryFlagsJson", typeof(string), true), ("Version", typeof(int), true)]);
        state.HasKey("UserId"); state.Property<int>("UserId").ValueGeneratedNever();
        state.Property<int>("Version").IsConcurrencyToken();
        var quest = Entity(model, "StoryQuestProgress", "StoryQuestProgress", [
            ("UserId", typeof(int), true), ("QuestCode", typeof(string), true),
            ("DefinitionJson", typeof(string), true), ("ActorCharacterId", typeof(int?), false),
            ("Status", typeof(string), true), ("Progress", typeof(int), true),
            ("ActivatedAtUtc", typeof(DateTime), true), ("CompletedAtUtc", typeof(DateTime?), false),
            ("TurnedInAtUtc", typeof(DateTime?), false), ("Version", typeof(int), true)]);
        quest.HasKey("UserId", "QuestCode"); quest.Property<int>("Version").IsConcurrencyToken();
        quest.ToTable("StoryQuestProgress", t => t.HasCheckConstraint("CK_StoryQuestProgress_Progress", "Progress >= 0"));
        var map = Entity(model, "StoryMapUnlock", "StoryMapUnlocks", [
            ("UserId", typeof(int), true), ("MapNodeCode", typeof(string), true),
            ("SourceQuestCode", typeof(string), true), ("UnlockedAtUtc", typeof(DateTime), true)]);
        map.HasKey("UserId", "MapNodeCode");
        var receipt = Entity(model, "StoryActionReceipt", "StoryActionReceipts", [
            ("UserId", typeof(int), true), ("RequestId", typeof(string), true), ("Fingerprint", typeof(string), true),
            ("QuestCode", typeof(string), false), ("RecipientCharacterId", typeof(int), true),
            ("RewardsJson", typeof(string), true), ("CompletedAtUtc", typeof(DateTime), true)]);
        receipt.HasKey("UserId", "RequestId");
        receipt.HasIndex("UserId", "QuestCode").IsUnique().HasFilter("QuestCode IS NOT NULL");
        var evt = Entity(model, "StoryEventReceipt", "StoryEventReceipts", [
            ("UserId", typeof(int), true), ("QuestCode", typeof(string), true),
            ("SourceEventId", typeof(string), true), ("OccurredAtUtc", typeof(DateTime), true)]);
        evt.HasKey("UserId", "QuestCode", "SourceEventId");
    }
}
