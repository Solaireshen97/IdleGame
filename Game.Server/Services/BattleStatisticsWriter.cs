using System.Text.Json;
using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

/// <summary>Stages statistics in the battle's DbContext. Only the battle owner may commit.</summary>
public sealed class BattleStatisticsWriter(GameDbContext db)
{
    public async Task<BattleSettlementSnapshot> CaptureAsync(Room room, Monster monster,
        IReadOnlyList<BattleParticipant> party, DateTime now)
    {
        var ids = party.Select(p => p.Character.Id).ToArray();
        var elements = await db.CharacterWeapons.AsNoTracking()
            .Where(w => ids.Contains(w.CharacterId) && w.EquippedSlotIndex == WeaponRules.MainSlotIndex)
            .ToDictionaryAsync(w => w.CharacterId, w => w.Element);
        return new()
        {
            RoomId = room.Id, RunSequence = room.RunSequence, RoundNumber = room.RoundNumber + 1,
            DungeonId = room.DungeonId, DepthLevel = room.DepthLevel, SettledAtUtc = now,
            Enemy = new(monster.Id, monster.Name, monster.WaveNumber, monster.Position, monster.IsBoss),
            Participants = party.Select(p =>
            {
                var element = elements.TryGetValue(p.Character.Id, out var value) ? value : (Game.Shared.Enums.ElementType?)null;
                return new BattleStatisticsParticipantSnapshot(p.Character.Id, p.Slot.UserId ?? p.Character.UserId,
                    p.Character.Name, p.Character.ProfessionCode, element, p.Slot.SlotIndex, p.Character.Hp,
                    new() { ProfessionCode = p.Character.ProfessionCode, Element = element,
                        FormationName = p.Slot.SourceFormationName, FormationVersion = p.Slot.SourceFormationVersion,
                        LoadoutHash = p.Slot.AppliedLoadoutJson is { } json ? CombatLoadoutCodec.Hash(json) : "" });
            }).ToArray()
        };
    }

    public async Task<bool> ApplyAsync(Room room, BattleSettlementSnapshot batch)
    {
        if (batch.RoomId != room.Id || batch.RunSequence != room.RunSequence ||
            batch.RoundNumber != room.RoundNumber || batch.SettlementVersion != room.Version)
            throw new InvalidOperationException("Statistics cannot be committed outside their battle settlement.");
        var delta = BattleStatisticsReducer.Reduce(batch);
        var run = await db.BattleRunStatistics.FindAsync(batch.RoomId, batch.RunSequence);
        if (run is null)
        {
            // A pre-existing battle may have been running before statistics were installed.
            run = await CreateRunAsync(room, batch.SettledAtUtc, batch.RoundNumber);
        }
        if (batch.RoundNumber <= run.LastAggregatedRound && batch.SettlementVersion <= run.LastSettlementVersion)
            return false;
        if (batch.RoundNumber != run.LastAggregatedRound + 1 || batch.SettlementVersion <= run.LastSettlementVersion)
            throw new InvalidOperationException("A statistics settlement is missing or out of order.");

        var encounter = await db.BattleEncounterStatistics.FindAsync(batch.RoomId, batch.RunSequence, batch.Enemy.MonsterId);
        if (encounter is null)
        {
            encounter = delta.Encounter;
            db.BattleEncounterStatistics.Add(encounter);
        }
        else
        {
            checked
            {
                encounter.RecordedRounds++;
                encounter.UnattributedDamage += delta.Encounter.UnattributedDamage;
                encounter.UnattributedHealing += delta.Encounter.UnattributedHealing;
            }
            encounter.LastRound = batch.RoundNumber;
        }

        // One bounded query for the run's roster also resolves persistent effects from departed actors.
        var knownActors = await db.BattleActorStatistics
            .Where(a => a.RoomId == batch.RoomId && a.RunSequence == batch.RunSequence).ToListAsync();
        var current = knownActors.Where(a => a.MonsterId == batch.Enemy.MonsterId).ToDictionary(a => a.CharacterId);
        foreach (var increment in delta.Actors)
        {
            if (!current.TryGetValue(increment.CharacterId, out var actor))
            {
                actor = increment;
                if (increment.PresentRounds == 0)
                {
                    var previous = knownActors.Where(a => a.CharacterId == increment.CharacterId)
                        .OrderByDescending(a => a.LastObservedRound).FirstOrDefault();
                    if (previous is not null)
                    {
                        actor.UserId = previous.UserId;
                        if (string.IsNullOrEmpty(actor.Name)) actor.Name = previous.Name;
                        if (string.IsNullOrEmpty(actor.ProfessionCode)) actor.ProfessionCode = previous.ProfessionCode;
                        actor.Element ??= previous.Element;
                    }
                }
                db.BattleActorStatistics.Add(actor);
                current.Add(actor.CharacterId, actor);
            }
            else
            {
                AddMetrics(actor, increment);
                if (increment.PresentRounds > 0)
                {
                    actor.UserId = increment.UserId;
                    if (string.IsNullOrEmpty(actor.Name)) actor.Name = increment.Name;
                    if (string.IsNullOrEmpty(actor.ProfessionCode)) actor.ProfessionCode = increment.ProfessionCode;
                    actor.Element ??= increment.Element;
                    actor.SlotIndex = increment.SlotIndex;
                    actor.LastObservedRound = increment.LastObservedRound;
                    actor.WasAlive = increment.WasAlive;
                    actor.ConfigurationsJson = MergeConfigurations(actor.ConfigurationsJson, increment.ConfigurationsJson);
                }
            }
        }

        var abilities = await db.BattleAbilityStatistics.Where(a => a.RoomId == batch.RoomId &&
                a.RunSequence == batch.RunSequence && a.MonsterId == batch.Enemy.MonsterId)
            .ToDictionaryAsync(a => new { a.CharacterId, a.ActionKind, a.SourceCode });
        foreach (var increment in delta.Abilities)
        {
            if (!abilities.TryGetValue(new { increment.CharacterId, increment.ActionKind, increment.SourceCode }, out var ability))
                db.BattleAbilityStatistics.Add(increment);
            else
            {
                checked
                {
                    ability.DamageDealt += increment.DamageDealt;
                    ability.HealingDone += increment.HealingDone;
                    ability.SelfHealing += increment.SelfHealing;
                    ability.PotionHealing += increment.PotionHealing;
                }
            }
        }
        run.RecordedRounds = checked(run.RecordedRounds + 1);
        run.LastAggregatedRound = batch.RoundNumber;
        run.LastSettlementVersion = batch.SettlementVersion;
        run.Outcome = batch.Outcome;
        run.EndedAtUtc = batch.Outcome == "InProgress" ? null : batch.SettledAtUtc;
        return true;
    }

    public async Task RegisterRunAsync(Room room, DateTime now)
    {
        if (await db.BattleRunStatistics.FindAsync(room.Id, room.RunSequence) is null)
            await CreateRunAsync(room, now, room.RoundNumber + 1);
    }

    public async Task FinishRunAsync(Room room, DateTime now, string outcome = "Stopped")
    {
        var run = await db.BattleRunStatistics.FindAsync(room.Id, room.RunSequence);
        // Missing pre-installation history must remain missing. A pending first round creates its row during Apply.
        if (run is null || run.Outcome != "InProgress") return;
        run.Outcome = outcome;
        run.EndedAtUtc = now;
    }

    private async Task<BattleRunStatistics> CreateRunAsync(Room room, DateTime now, int coverageStartRound)
    {
        var rules = await db.DungeonRunRuleSnapshots.FindAsync(room.Id);
        var run = new BattleRunStatistics
        {
            RoomId = room.Id, RunSequence = room.RunSequence, DungeonId = room.DungeonId, DepthLevel = room.DepthLevel,
            RulesRevision = rules?.Revision ?? "", StartedAtUtc = now, CoverageStartRound = coverageStartRound,
            LastAggregatedRound = coverageStartRound - 1
        };
        db.BattleRunStatistics.Add(run);
        return run;
    }

    private static string MergeConfigurations(string current, string incoming) => JsonSerializer.Serialize(
        (JsonSerializer.Deserialize<List<BattleStatisticsConfiguration>>(current) ?? [])
        .Concat(JsonSerializer.Deserialize<List<BattleStatisticsConfiguration>>(incoming) ?? []).Distinct().ToArray());

    private static void AddMetrics(BattleActorStatistics target, BattleActorStatistics delta)
    {
        checked
        {
            target.PresentRounds += delta.PresentRounds;
            target.AliveRounds += delta.AliveRounds;
            target.DamageDealt += delta.DamageDealt;
            target.DamageTaken += delta.DamageTaken;
            target.HealingDone += delta.HealingDone;
            target.HealingReceived += delta.HealingReceived;
            target.SelfHealing += delta.SelfHealing;
            target.PotionHealing += delta.PotionHealing;
            target.Deaths += delta.Deaths;
            target.Cleanses += delta.Cleanses;
            target.Dispels += delta.Dispels;
            target.Interrupts += delta.Interrupts;
        }
    }
}
