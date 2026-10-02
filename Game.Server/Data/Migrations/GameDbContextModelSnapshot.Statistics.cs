using Microsoft.EntityFrameworkCore;

namespace Game.Server.Data.Migrations;

partial class GameDbContextModelSnapshot
{
    private static void AddStatistics(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity("Game.Shared.Models.BattleRunStatistics", b =>
        {
            b.Property<int>("RoomId").HasColumnType("INTEGER");
            b.Property<int>("RunSequence").HasColumnType("INTEGER");
            b.Property<int>("SchemaVersion").HasColumnType("INTEGER");
            b.Property<int>("DungeonId").HasColumnType("INTEGER");
            b.Property<int>("DepthLevel").HasColumnType("INTEGER");
            b.Property<string>("RulesRevision").IsRequired().HasColumnType("TEXT");
            b.Property<DateTime>("StartedAtUtc").HasColumnType("TEXT");
            b.Property<DateTime?>("EndedAtUtc").HasColumnType("TEXT");
            b.Property<string>("Outcome").IsRequired().HasColumnType("TEXT");
            b.Property<int>("CoverageStartRound").HasColumnType("INTEGER");
            b.Property<long>("RecordedRounds").HasColumnType("INTEGER");
            b.Property<int>("LastAggregatedRound").HasColumnType("INTEGER");
            b.Property<int>("LastSettlementVersion").HasColumnType("INTEGER");
            b.HasKey("RoomId", "RunSequence");
            b.ToTable("BattleRunStatistics");
            b.HasOne("Game.Shared.Models.Room", null).WithMany().HasForeignKey("RoomId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        });
        modelBuilder.Entity("Game.Shared.Models.BattleEncounterStatistics", b =>
        {
            b.Property<int>("RoomId").HasColumnType("INTEGER");
            b.Property<int>("RunSequence").HasColumnType("INTEGER");
            b.Property<int>("MonsterId").HasColumnType("INTEGER");
            b.Property<string>("Name").IsRequired().HasColumnType("TEXT");
            b.Property<int>("WaveNumber").HasColumnType("INTEGER");
            b.Property<int>("Position").HasColumnType("INTEGER");
            b.Property<bool>("IsBoss").HasColumnType("INTEGER");
            b.Property<long>("RecordedRounds").HasColumnType("INTEGER");
            b.Property<int>("FirstRound").HasColumnType("INTEGER");
            b.Property<int>("LastRound").HasColumnType("INTEGER");
            b.Property<long>("UnattributedDamage").HasColumnType("INTEGER");
            b.Property<long>("UnattributedHealing").HasColumnType("INTEGER");
            b.HasKey("RoomId", "RunSequence", "MonsterId");
            b.ToTable("BattleEncounterStatistics");
            b.HasOne("Game.Shared.Models.Room", null).WithMany().HasForeignKey("RoomId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        });
        modelBuilder.Entity("Game.Shared.Models.BattleActorStatistics", b =>
        {
            b.Property<int>("RoomId").HasColumnType("INTEGER");
            b.Property<int>("RunSequence").HasColumnType("INTEGER");
            b.Property<int>("MonsterId").HasColumnType("INTEGER");
            b.Property<int>("CharacterId").HasColumnType("INTEGER");
            b.Property<int?>("UserId").HasColumnType("INTEGER");
            b.Property<string>("Name").IsRequired().HasColumnType("TEXT");
            b.Property<string>("ProfessionCode").IsRequired().HasColumnType("TEXT");
            b.Property<Game.Shared.Enums.ElementType?>("Element").HasColumnType("INTEGER");
            b.Property<int?>("SlotIndex").HasColumnType("INTEGER");
            b.Property<string>("ConfigurationsJson").IsRequired().HasColumnType("TEXT");
            b.Property<int>("LastObservedRound").HasColumnType("INTEGER");
            b.Property<bool>("WasAlive").HasColumnType("INTEGER");
            b.Property<long>("PresentRounds").HasColumnType("INTEGER");
            b.Property<long>("AliveRounds").HasColumnType("INTEGER");
            b.Property<long>("DamageDealt").HasColumnType("INTEGER");
            b.Property<long>("DamageTaken").HasColumnType("INTEGER");
            b.Property<long>("HealingDone").HasColumnType("INTEGER");
            b.Property<long>("HealingReceived").HasColumnType("INTEGER");
            b.Property<long>("SelfHealing").HasColumnType("INTEGER");
            b.Property<long>("PotionHealing").HasColumnType("INTEGER");
            b.Property<long>("Deaths").HasColumnType("INTEGER");
            b.Property<long>("Cleanses").HasColumnType("INTEGER");
            b.Property<long>("Dispels").HasColumnType("INTEGER");
            b.Property<long>("Interrupts").HasColumnType("INTEGER");
            b.HasKey("RoomId", "RunSequence", "MonsterId", "CharacterId");
            b.ToTable("BattleActorStatistics");
            b.HasOne("Game.Shared.Models.Room", null).WithMany().HasForeignKey("RoomId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        });
        modelBuilder.Entity("Game.Shared.Models.BattleAbilityStatistics", b =>
        {
            b.Property<int>("RoomId").HasColumnType("INTEGER");
            b.Property<int>("RunSequence").HasColumnType("INTEGER");
            b.Property<int>("MonsterId").HasColumnType("INTEGER");
            b.Property<int>("CharacterId").HasColumnType("INTEGER");
            b.Property<Game.Shared.Dtos.BattleActionKind>("ActionKind").HasColumnType("INTEGER");
            b.Property<string>("SourceCode").IsRequired().HasColumnType("TEXT");
            b.Property<string>("Label").IsRequired().HasColumnType("TEXT");
            b.Property<long>("DamageDealt").HasColumnType("INTEGER");
            b.Property<long>("HealingDone").HasColumnType("INTEGER");
            b.Property<long>("SelfHealing").HasColumnType("INTEGER");
            b.Property<long>("PotionHealing").HasColumnType("INTEGER");
            b.HasKey("RoomId", "RunSequence", "MonsterId", "CharacterId", "ActionKind", "SourceCode");
            b.ToTable("BattleAbilityStatistics");
            b.HasOne("Game.Shared.Models.Room", null).WithMany().HasForeignKey("RoomId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        });
    }
}
