using Game.Server.Configuration;
using Game.Shared.Models;

namespace Game.Server.Services;

/// <summary>Encounter clocks and transitions shared by all phases. State is saved by the round transaction.</summary>
internal static class MonsterPhaseLifecycle
{
    public static bool PrepareRound(BattleMonsterPhaseState state, int round)
    {
        if (state.LastPreparedRound == round) return false;
        state.LastPreparedRound = round;
        return true;
    }

    public static bool ShouldActivate(IMonsterPhaseSchedule schedule, BattleMonsterPhaseState state,
        Monster monster, int round) => schedule.TriggerHpPercent is { } hp
        ? state.ActivationCount == 0 && (long)monster.Hp * 100 <= (long)monster.MaxHp * hp
        : round - state.EncounterStartRound + 1 >= state.NextActivationRound;

    public static void Activate(IMonsterPhaseSchedule schedule, BattleMonsterPhaseState state, int round)
    {
        if (schedule.TriggerHpPercent is null)
        {
            var localRound = round - state.EncounterStartRound + 1;
            // Preserve the original cadence even when a caller resumes after a gap.
            var elapsedCycles = (localRound - state.NextActivationRound) / schedule.CycleRounds + 1;
            state.NextActivationRound = checked(state.NextActivationRound + elapsedCycles * schedule.CycleRounds);
        }
        state.IsActive = true;
        state.ElementDamage = 0;
        state.ExpiresAfterRound = checked(round + schedule.WindowRounds - 1);
        state.LastActivationRound = round;
        state.ActivationCount++;
    }

    public static void Complete(BattleMonsterPhaseState state, int round)
    {
        state.IsActive = false;
        state.BreakCount++;
        state.LastBreakRound = round;
        state.RewardStartsAtRound = checked(round + 1);
    }
}
