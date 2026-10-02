using System.Text.Json;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;

namespace Game.Server.Services;

public sealed record BattleStatisticsParticipantSnapshot(int CharacterId, int? UserId, string Name,
    string ProfessionCode, ElementType? Element, int SlotIndex, int Hp, BattleStatisticsConfiguration Configuration);

public sealed record BattleStatisticsEnemySnapshot(int MonsterId, string Name, int WaveNumber, int Position, bool IsBoss);

/// <summary>Frozen before switching enemies or clearing room slots. Version/events are set just before committing.</summary>
public sealed record BattleSettlementSnapshot
{
    public int RoomId { get; init; }
    public int RunSequence { get; init; }
    public int RoundNumber { get; init; }
    public int SettlementVersion { get; init; }
    public int DungeonId { get; init; }
    public int DepthLevel { get; init; }
    public DateTime SettledAtUtc { get; init; }
    public required BattleStatisticsEnemySnapshot Enemy { get; init; }
    public IReadOnlyList<BattleStatisticsParticipantSnapshot> Participants { get; init; } = [];
    public IReadOnlyList<BattleEventResponse> Events { get; init; } = [];
    public string Outcome { get; init; } = "InProgress";
}

public sealed record BattleStatisticsDelta(BattleEncounterStatistics Encounter,
    IReadOnlyList<BattleActorStatistics> Actors, IReadOnlyList<BattleAbilityStatistics> Abilities);

/// <summary>Consumes one complete settlement, never display strings or retained playback history.</summary>
public static class BattleStatisticsReducer
{
    public static BattleStatisticsDelta Reduce(BattleSettlementSnapshot batch)
    {
        if (batch.RoomId <= 0 || batch.RunSequence <= 0 || batch.RoundNumber <= 0 || batch.Enemy.MonsterId <= 0)
            throw new ArgumentException("Statistics require a valid settlement identity.", nameof(batch));
        var encounter = new BattleEncounterStatistics
        {
            RoomId = batch.RoomId, RunSequence = batch.RunSequence, MonsterId = batch.Enemy.MonsterId,
            Name = batch.Enemy.Name, WaveNumber = batch.Enemy.WaveNumber, Position = batch.Enemy.Position,
            IsBoss = batch.Enemy.IsBoss, RecordedRounds = 1, FirstRound = batch.RoundNumber, LastRound = batch.RoundNumber
        };
        var actors = new Dictionary<int, BattleActorStatistics>();
        var abilities = new Dictionary<(int CharacterId, BattleActionKind Kind, string Code), BattleAbilityStatistics>();

        BattleActorStatistics Actor(int id, BattleEventActor? reference = null)
        {
            if (id <= 0) throw new ArgumentException("Invalid statistics actor identity.", nameof(batch));
            if (!actors.TryGetValue(id, out var actor))
            {
                actor = new() { RoomId = batch.RoomId, RunSequence = batch.RunSequence, MonsterId = batch.Enemy.MonsterId,
                    CharacterId = id, Name = reference?.Name ?? "", ProfessionCode = reference?.ProfessionCode ?? "" };
                actors.Add(id, actor);
            }
            return actor;
        }

        BattleAbilityStatistics Ability(BattleEventResponse fact, int id)
        {
            var code = string.IsNullOrWhiteSpace(fact.SkillCode) ? "unknown" : fact.SkillCode;
            var key = (id, fact.ActionKind, code);
            if (!abilities.TryGetValue(key, out var ability))
            {
                ability = new() { RoomId = batch.RoomId, RunSequence = batch.RunSequence, MonsterId = batch.Enemy.MonsterId,
                    CharacterId = id, ActionKind = fact.ActionKind, SourceCode = code, Label = fact.Label };
                abilities.Add(key, ability);
            }
            return ability;
        }

        foreach (var participant in batch.Participants)
        {
            if (actors.ContainsKey(participant.CharacterId))
                throw new ArgumentException("A participant may occupy only one statistics row per settlement.", nameof(batch));
            var actor = Actor(participant.CharacterId);
            actor.UserId = participant.UserId;
            actor.Name = participant.Name;
            actor.ProfessionCode = participant.ProfessionCode;
            actor.Element = participant.Element;
            actor.SlotIndex = participant.SlotIndex;
            actor.ConfigurationsJson = JsonSerializer.Serialize(new[] { participant.Configuration });
            actor.LastObservedRound = batch.RoundNumber;
            actor.WasAlive = participant.Hp > 0;
            actor.PresentRounds = 1;
            actor.AliveRounds = participant.Hp > 0 ? 1 : 0;
        }

        var sequences = new HashSet<int>();
        checked
        {
            foreach (var fact in batch.Events)
            {
                if (fact.RoomId != batch.RoomId || fact.RunSequence != batch.RunSequence ||
                    fact.RoundNumber != batch.RoundNumber || fact.MonsterId != batch.Enemy.MonsterId ||
                    fact.SettlementVersion != batch.SettlementVersion || fact.Sequence <= 0 || !sequences.Add(fact.Sequence))
                    throw new ArgumentException("Events must belong to one complete, non-duplicated settlement.", nameof(batch));
                if (fact.ActualAmount < 0) throw new ArgumentException("Negative statistics amount.", nameof(batch));

                var source = fact.Source is { ActorType: "Character", ActorId: > 0 } player ? player : null;
                if (fact.Kind == BattleEventKind.Damage)
                {
                    if (fact.Target.ActorType == "Monster")
                    {
                        if (source is not null)
                        {
                            Actor(source.ActorId, source).DamageDealt += fact.ActualAmount;
                            Ability(fact, source.ActorId).DamageDealt += fact.ActualAmount;
                        }
                        else encounter.UnattributedDamage += fact.ActualAmount;
                    }
                    else if (fact.Target.ActorType == "Character")
                    {
                        var target = Actor(fact.Target.ActorId, fact.Target);
                        target.DamageTaken += fact.ActualAmount;
                        if (fact.HpBefore is > 0 && fact.HpAfter == 0)
                        {
                            target.Deaths++;
                            target.WasAlive = false;
                        }
                    }
                }
                else if (fact.Kind == BattleEventKind.Heal && fact.Target.ActorType == "Character")
                {
                    Actor(fact.Target.ActorId, fact.Target).HealingReceived += fact.ActualAmount;
                    if (source is null) { encounter.UnattributedHealing += fact.ActualAmount; continue; }
                    var actor = Actor(source.ActorId, source);
                    var ability = Ability(fact, source.ActorId);
                    actor.HealingDone += fact.ActualAmount;
                    ability.HealingDone += fact.ActualAmount;
                    if (source.ActorId == fact.Target.ActorId)
                    {
                        actor.SelfHealing += fact.ActualAmount;
                        ability.SelfHealing += fact.ActualAmount;
                    }
                    if (fact.ActionKind == BattleActionKind.Consumable)
                    {
                        actor.PotionHealing += fact.ActualAmount;
                        ability.PotionHealing += fact.ActualAmount;
                    }
                }
                else if (source is not null)
                {
                    if (fact.Kind == BattleEventKind.Cleanse && fact.StatusChange == BattleStatusChange.Removed)
                        Actor(source.ActorId, source).Cleanses++;
                    else if (fact.Kind == BattleEventKind.Dispel && fact.StatusChange == BattleStatusChange.Removed)
                        Actor(source.ActorId, source).Dispels++;
                    else if (fact.Kind == BattleEventKind.Interrupt)
                        Actor(source.ActorId, source).Interrupts++;
                }
            }
        }
        return new(encounter, actors.Values.ToList(), abilities.Values.ToList());
    }
}
