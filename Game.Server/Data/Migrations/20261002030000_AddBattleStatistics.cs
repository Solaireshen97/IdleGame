using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Game.Server.Data.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration("20261002030000_AddBattleStatistics")]
public sealed class AddBattleStatistics : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
            CREATE TABLE BattleRunStatistics (
                RoomId INTEGER NOT NULL,
                RunSequence INTEGER NOT NULL,
                SchemaVersion INTEGER NOT NULL,
                DungeonId INTEGER NOT NULL,
                DepthLevel INTEGER NOT NULL,
                RulesRevision TEXT NOT NULL,
                StartedAtUtc TEXT NOT NULL,
                EndedAtUtc TEXT NULL,
                Outcome TEXT NOT NULL,
                CoverageStartRound INTEGER NOT NULL,
                RecordedRounds INTEGER NOT NULL,
                LastAggregatedRound INTEGER NOT NULL,
                LastSettlementVersion INTEGER NOT NULL,
                PRIMARY KEY (RoomId, RunSequence),
                FOREIGN KEY (RoomId) REFERENCES Rooms(Id) ON DELETE CASCADE
            );
            CREATE TABLE BattleEncounterStatistics (
                RoomId INTEGER NOT NULL,
                RunSequence INTEGER NOT NULL,
                MonsterId INTEGER NOT NULL,
                Name TEXT NOT NULL,
                WaveNumber INTEGER NOT NULL,
                Position INTEGER NOT NULL,
                IsBoss INTEGER NOT NULL,
                RecordedRounds INTEGER NOT NULL,
                FirstRound INTEGER NOT NULL,
                LastRound INTEGER NOT NULL,
                UnattributedDamage INTEGER NOT NULL,
                UnattributedHealing INTEGER NOT NULL,
                PRIMARY KEY (RoomId, RunSequence, MonsterId),
                FOREIGN KEY (RoomId) REFERENCES Rooms(Id) ON DELETE CASCADE
            );
            CREATE TABLE BattleActorStatistics (
                RoomId INTEGER NOT NULL,
                RunSequence INTEGER NOT NULL,
                MonsterId INTEGER NOT NULL,
                CharacterId INTEGER NOT NULL,
                UserId INTEGER NULL,
                Name TEXT NOT NULL,
                ProfessionCode TEXT NOT NULL,
                Element INTEGER NULL,
                SlotIndex INTEGER NULL,
                ConfigurationsJson TEXT NOT NULL,
                LastObservedRound INTEGER NOT NULL,
                WasAlive INTEGER NOT NULL,
                PresentRounds INTEGER NOT NULL,
                AliveRounds INTEGER NOT NULL,
                DamageDealt INTEGER NOT NULL,
                DamageTaken INTEGER NOT NULL,
                HealingDone INTEGER NOT NULL,
                HealingReceived INTEGER NOT NULL,
                SelfHealing INTEGER NOT NULL,
                PotionHealing INTEGER NOT NULL,
                Deaths INTEGER NOT NULL,
                Cleanses INTEGER NOT NULL,
                Dispels INTEGER NOT NULL,
                Interrupts INTEGER NOT NULL,
                PRIMARY KEY (RoomId, RunSequence, MonsterId, CharacterId),
                FOREIGN KEY (RoomId) REFERENCES Rooms(Id) ON DELETE CASCADE
            );
            CREATE TABLE BattleAbilityStatistics (
                RoomId INTEGER NOT NULL,
                RunSequence INTEGER NOT NULL,
                MonsterId INTEGER NOT NULL,
                CharacterId INTEGER NOT NULL,
                ActionKind INTEGER NOT NULL,
                SourceCode TEXT NOT NULL,
                Label TEXT NOT NULL,
                DamageDealt INTEGER NOT NULL,
                HealingDone INTEGER NOT NULL,
                SelfHealing INTEGER NOT NULL,
                PotionHealing INTEGER NOT NULL,
                PRIMARY KEY (RoomId, RunSequence, MonsterId, CharacterId, ActionKind, SourceCode),
                FOREIGN KEY (RoomId) REFERENCES Rooms(Id) ON DELETE CASCADE
            );
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
            DROP TABLE BattleAbilityStatistics;
            DROP TABLE BattleActorStatistics;
            DROP TABLE BattleEncounterStatistics;
            DROP TABLE BattleRunStatistics;
        """);
}
