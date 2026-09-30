using Game.Shared.Dtos;
using Game.Shared.Enums;

namespace Game.BalanceSimulator;

/// <summary>Counts committed facts; display names and log strings are never inputs.</summary>
public sealed class SimulatorMetrics(IEnumerable<int> bossIds)
{
    private readonly HashSet<int> _bosses = bossIds.ToHashSet();
    private readonly HashSet<(int Run, int Round, int Monster, string Code)> _skillRounds = [];
    private readonly HashSet<(int Run, int Round, int Sequence, int Character)> _deaths = [];
    public int CharacterDeaths => _deaths.Count;
    public Dictionary<string, int> BossSkillUses => _skillRounds.GroupBy(value => value.Code, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    public void Observe(IEnumerable<BattleEventResponse> facts)
    {
        foreach (var fact in facts)
        {
            if (fact.Source is { ActorType: "Monster" } source && _bosses.Contains(source.ActorId) &&
                fact.ActionKind == BattleActionKind.Skill && !string.IsNullOrEmpty(fact.SkillCode))
                _skillRounds.Add((fact.RunSequence, fact.RoundNumber, source.ActorId, fact.SkillCode));
            if (fact.Kind == BattleEventKind.Damage && fact.Target.ActorType == "Character" &&
                fact.HpBefore is > 0 && fact.HpAfter == 0)
                _deaths.Add((fact.RunSequence, fact.RoundNumber, fact.Sequence, fact.Target.ActorId));
        }
    }

    public static string FailureCategory(bool victory, RoomStatus status, int survivors, int rounds, int roundLimit) =>
        victory ? "None" : survivors == 0 ? "PartyDefeated" : rounds >= roundLimit ? "RoundLimit" :
        status == RoomStatus.BattleOver ? "BattleOverWithoutVictory" : "StepLimit";
}
