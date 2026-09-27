using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260928010000_AddDungeonDepthMastery")]
public sealed class AddDungeonDepthMastery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("DepthLevel", "Rooms", "INTEGER", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<string>("DepthDefinitionJson", "Rooms", "TEXT", nullable: true);
        migrationBuilder.AddColumn<int>("HighestDepth", "UserDungeonClears", "INTEGER", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<int>("Version", "UserDungeonClears", "INTEGER", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<string>("RewardSource", "RewardEntries", "TEXT", nullable: false, defaultValue: "Base");
        migrationBuilder.CreateTable("CharacterDungeonProgress", columns: table => new
        {
            CharacterId = table.Column<int>("INTEGER", nullable: false),
            DungeonId = table.Column<int>("INTEGER", nullable: false),
            HighestDepth = table.Column<int>("INTEGER", nullable: false),
            Version = table.Column<int>("INTEGER", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_CharacterDungeonProgress", item => new { item.CharacterId, item.DungeonId });
            table.CheckConstraint("CK_CharacterDungeonProgress_Depth", "HighestDepth >= 1");
        });
        migrationBuilder.CreateTable("DungeonRunParticipants", columns: table => new
        {
            RoomId = table.Column<int>("INTEGER", nullable: false),
            RunSequence = table.Column<int>("INTEGER", nullable: false),
            CharacterId = table.Column<int>("INTEGER", nullable: false),
            MasteryLevel = table.Column<int>("INTEGER", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_DungeonRunParticipants", item => new { item.RoomId, item.RunSequence, item.CharacterId });
            table.ForeignKey("FK_DungeonRunParticipants_Rooms_RoomId", item => item.RoomId, "Rooms", "Id", onDelete: ReferentialAction.Cascade);
            table.CheckConstraint("CK_DungeonRunParticipants_Mastery", "MasteryLevel BETWEEN 0 AND 4");
        });
        // Only actual character evidence earns mastery; account records never promote other characters.
        migrationBuilder.Sql("""
            INSERT INTO CharacterDungeonProgress (CharacterId, DungeonId, HighestDepth, Version)
            SELECT m.CharacterId, d.Id, 1, 0 FROM CharacterBattleMilestones m
            JOIN Dungeons d ON d.Code = m.TargetCode JOIN Characters c ON c.Id = m.CharacterId
            WHERE m.Kind = 'DungeonClear' AND m.Count > 0;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("DungeonRunParticipants");
        migrationBuilder.DropTable("CharacterDungeonProgress");
        migrationBuilder.Sql("""
            ALTER TABLE RewardEntries DROP COLUMN RewardSource;
            ALTER TABLE UserDungeonClears DROP COLUMN HighestDepth;
            ALTER TABLE UserDungeonClears DROP COLUMN Version;
            ALTER TABLE Rooms DROP COLUMN DepthLevel;
            ALTER TABLE Rooms DROP COLUMN DepthDefinitionJson;
            """);
    }
}
