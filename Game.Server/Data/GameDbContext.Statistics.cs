using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Data;

public partial class GameDbContext
{
    public DbSet<BattleRunStatistics> BattleRunStatistics => Set<BattleRunStatistics>();
    public DbSet<BattleEncounterStatistics> BattleEncounterStatistics => Set<BattleEncounterStatistics>();
    public DbSet<BattleActorStatistics> BattleActorStatistics => Set<BattleActorStatistics>();
    public DbSet<BattleAbilityStatistics> BattleAbilityStatistics => Set<BattleAbilityStatistics>();

    private static void ConfigureStatistics(ModelBuilder model)
    {
        model.Entity<BattleRunStatistics>().HasKey(x => new { x.RoomId, x.RunSequence });
        model.Entity<BattleRunStatistics>().HasOne<Room>().WithMany().HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<BattleEncounterStatistics>().HasKey(x => new { x.RoomId, x.RunSequence, x.MonsterId });
        model.Entity<BattleEncounterStatistics>().HasOne<Room>().WithMany().HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<BattleActorStatistics>().HasKey(x => new { x.RoomId, x.RunSequence, x.MonsterId, x.CharacterId });
        model.Entity<BattleActorStatistics>().HasOne<Room>().WithMany().HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<BattleAbilityStatistics>().HasKey(x => new { x.RoomId, x.RunSequence, x.MonsterId, x.CharacterId, x.ActionKind, x.SourceCode });
        model.Entity<BattleAbilityStatistics>().HasOne<Room>().WithMany().HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);
    }
}
