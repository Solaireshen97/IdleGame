using Game.Shared.Models;

namespace Game.Server.Services;

/// <summary>Prepares frozen rules and derived encounter attributes before either reads or actions.</summary>
public sealed class BattleContextPreparation(DungeonRunRulesService? rules = null, MonsterPhaseService? phases = null)
{
    public async Task PrepareAsync(Room room, Monster monster, IReadOnlyList<BattleParticipant> party)
    {
        if (rules is not null) await rules.EnsureAsync(room);
        if (phases is not null) await phases.RefreshPlagueHealthAsync(room, monster, party);
    }
}
