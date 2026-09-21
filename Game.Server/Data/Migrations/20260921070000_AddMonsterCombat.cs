using Game.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20260921070000_AddMonsterCombat")]
public partial class AddMonsterCombat : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("CombatProfileCode", "Monsters", type: "TEXT", nullable: false, defaultValue: "");

        migrationBuilder.Sql("""
            UPDATE Monsters
            SET CombatProfileCode = CASE
                WHEN WaveNumber = 2 AND Position = 1 THEN 'slime-acid'
                WHEN WaveNumber = 2 AND Position = 2 THEN 'slime-hardened'
                WHEN WaveNumber = 3 AND Position = 1 THEN 'king-slime'
                ELSE CombatProfileCode
            END
            WHERE RoomId IN (
                SELECT Rooms.Id
                FROM Rooms
                INNER JOIN Dungeons ON Dungeons.Id = Rooms.DungeonId
                WHERE Dungeons.Code = 'slime-field'
            );
            """);

        migrationBuilder.CreateTable("BattleMonsterSkillCooldowns", table => new
        {
            Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
            RoomId = table.Column<int>(type: "INTEGER", nullable: false),
            MonsterId = table.Column<int>(type: "INTEGER", nullable: false),
            SkillCode = table.Column<string>(type: "TEXT", nullable: false),
            ReadyAtRound = table.Column<int>(type: "INTEGER", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_BattleMonsterSkillCooldowns", row => row.Id));

        migrationBuilder.CreateTable("BattleStatusEffects", table => new
        {
            Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
            RoomId = table.Column<int>(type: "INTEGER", nullable: false),
            RunSequence = table.Column<int>(type: "INTEGER", nullable: false),
            TargetType = table.Column<string>(type: "TEXT", nullable: false),
            TargetId = table.Column<int>(type: "INTEGER", nullable: false),
            EffectCode = table.Column<string>(type: "TEXT", nullable: false),
            Stacks = table.Column<int>(type: "INTEGER", nullable: false),
            AppliedRound = table.Column<int>(type: "INTEGER", nullable: false),
            ExpiresAfterRound = table.Column<int>(type: "INTEGER", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_BattleStatusEffects", row => row.Id);
            table.CheckConstraint("CK_BattleStatusEffects_Values", "Stacks > 0 AND ExpiresAfterRound >= AppliedRound");
        });

        migrationBuilder.CreateTable("MonsterIntents", table => new
        {
            Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
            RoomId = table.Column<int>(type: "INTEGER", nullable: false),
            RunSequence = table.Column<int>(type: "INTEGER", nullable: false),
            RoundNumber = table.Column<int>(type: "INTEGER", nullable: false),
            MonsterId = table.Column<int>(type: "INTEGER", nullable: false),
            ActionType = table.Column<string>(type: "TEXT", nullable: false),
            SkillCode = table.Column<string>(type: "TEXT", nullable: true),
            TargetType = table.Column<string>(type: "TEXT", nullable: false),
            TargetCharacterId = table.Column<int>(type: "INTEGER", nullable: true),
            CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_MonsterIntents", row => row.Id));

        migrationBuilder.CreateIndex("IX_BattleMonsterSkillCooldowns_RoomId_MonsterId_SkillCode",
            "BattleMonsterSkillCooldowns", new[] { "RoomId", "MonsterId", "SkillCode" }, unique: true);
        migrationBuilder.CreateIndex("IX_BattleStatusEffects_RoomId_RunSequence_TargetType_TargetId_EffectCode",
            "BattleStatusEffects", new[] { "RoomId", "RunSequence", "TargetType", "TargetId", "EffectCode" }, unique: true);
        migrationBuilder.CreateIndex("IX_MonsterIntents_RoomId_RunSequence_RoundNumber_MonsterId",
            "MonsterIntents", new[] { "RoomId", "RunSequence", "RoundNumber", "MonsterId" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("BattleMonsterSkillCooldowns");
        migrationBuilder.DropTable("BattleStatusEffects");
        migrationBuilder.DropTable("MonsterIntents");
        migrationBuilder.DropColumn("CombatProfileCode", "Monsters");
    }
}
