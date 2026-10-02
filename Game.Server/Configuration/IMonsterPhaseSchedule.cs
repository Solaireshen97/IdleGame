namespace Game.Server.Configuration;

/// <summary>Shared scheduling contract; each phase retains its own effects and success conditions.</summary>
public interface IMonsterPhaseSchedule
{
    int? TriggerHpPercent { get; }
    int FirstActivationRound { get; }
    int CycleRounds { get; }
    int WindowRounds { get; }
}
