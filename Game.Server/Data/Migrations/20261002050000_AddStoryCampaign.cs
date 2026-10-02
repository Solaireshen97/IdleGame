using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20261002050000_AddStoryCampaign")]
public sealed class AddStoryCampaign : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE UserStoryStates (
                UserId INTEGER NOT NULL PRIMARY KEY REFERENCES Users(Id) ON DELETE CASCADE,
                IsLegacy INTEGER NOT NULL, TutorialCharacterId INTEGER NULL, CurrentQuestCode TEXT NULL,
                ChapterCode TEXT NOT NULL, ChapterCompleted INTEGER NOT NULL, StoryFlagsJson TEXT NOT NULL, Version INTEGER NOT NULL);
            CREATE TABLE StoryQuestProgress (
                UserId INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE, QuestCode TEXT NOT NULL,
                DefinitionJson TEXT NOT NULL, ActorCharacterId INTEGER NULL, Status TEXT NOT NULL, Progress INTEGER NOT NULL,
                ActivatedAtUtc TEXT NOT NULL, CompletedAtUtc TEXT NULL, TurnedInAtUtc TEXT NULL, Version INTEGER NOT NULL,
                PRIMARY KEY (UserId, QuestCode), CONSTRAINT CK_StoryQuestProgress_Progress CHECK (Progress >= 0));
            CREATE TABLE StoryMapUnlocks (
                UserId INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE, MapNodeCode TEXT NOT NULL,
                SourceQuestCode TEXT NOT NULL, UnlockedAtUtc TEXT NOT NULL, PRIMARY KEY (UserId, MapNodeCode));
            CREATE TABLE StoryActionReceipts (
                UserId INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE, RequestId TEXT NOT NULL, Fingerprint TEXT NOT NULL,
                QuestCode TEXT NULL, RecipientCharacterId INTEGER NOT NULL, RewardsJson TEXT NOT NULL, CompletedAtUtc TEXT NOT NULL,
                PRIMARY KEY (UserId, RequestId));
            CREATE UNIQUE INDEX IX_StoryActionReceipts_UserId_QuestCode ON StoryActionReceipts(UserId, QuestCode) WHERE QuestCode IS NOT NULL;
            CREATE TABLE StoryEventReceipts (
                UserId INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE, QuestCode TEXT NOT NULL,
                SourceEventId TEXT NOT NULL, OccurredAtUtc TEXT NOT NULL, PRIMARY KEY (UserId, QuestCode, SourceEventId));
            INSERT INTO UserStoryStates (UserId, IsLegacy, TutorialCharacterId, CurrentQuestCode, ChapterCode, ChapterCompleted, StoryFlagsJson, Version)
                SELECT Id, 1, ActiveCharacterId, NULL, 'ch01', 0, '[]', 0 FROM Users;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("StoryEventReceipts");
        migrationBuilder.DropTable("StoryActionReceipts");
        migrationBuilder.DropTable("StoryMapUnlocks");
        migrationBuilder.DropTable("StoryQuestProgress");
        migrationBuilder.DropTable("UserStoryStates");
    }
}
