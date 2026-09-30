using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Game.Shared.Dtos;
using Game.Shared.Models;

namespace Game.Server.Services;

/// <summary>Stable combat facts shared by regression tests and the balance simulator.</summary>
public sealed record BattleRoundTrace(JsonElement Data, string Fingerprint)
{
    public const int SchemaVersion = 1;

    public static BattleRoundTrace Capture(Room room, BattleResult result,
        IReadOnlyCollection<BattleParticipant> party, IReadOnlyCollection<Monster> monsters,
        IEnumerable<BattleStatusEffect> statuses, IEnumerable<BattleSkillCooldown> characterCooldowns,
        IEnumerable<BattleMonsterSkillCooldown> monsterCooldowns,
        IEnumerable<BattleConsumableCooldown>? consumableCooldowns = null,
        IEnumerable<BattleHealingPotionState>? healingPotions = null,
        IEnumerable<BattleOperationPotionState>? operationPotions = null,
        IEnumerable<BattleConsumableBuff>? consumableBuffs = null)
    {
        var identities = party.ToDictionary(entry => ("Character", entry.Character.Id),
            entry => $"character:{entry.Slot.SlotIndex}");
        foreach (var monster in monsters)
            identities.Add(("Monster", monster.Id), $"monster:{monster.WaveNumber}:{monster.Position}");
        string? Actor(string? type, int? id) => type is null || id is null ? null :
            identities.TryGetValue((type, id.Value), out var identity) ? identity :
            throw new InvalidOperationException($"Trace actor is outside the supplied encounter: {type}/{id}");
        object? Status(BattleStatusSnapshot? state) => state is null ? null : new
        {
            state.Code, state.EffectType, state.IsPositive, state.CanDispel, state.Stacks, state.RemainingRounds,
            state.Lifetime, state.CounterKind, state.Mechanic, state.AppliedRound, state.ExpiresAfterRound,
            state.PerTickValue, state.MagnitudeSnapshot,
            Source = Actor(state.SourceActorType, state.SourceActorId), state.SourceSkillCode,
            BoundTarget = Actor(state.BoundTargetType, state.BoundTargetId)
        };
        var data = new
        {
            SchemaVersion, room.RunSequence, room.RoundNumber, room.CurrentWaveNumber, room.TotalWaveCount,
            ActiveMonster = Actor("Monster", room.MonsterId), room.Status, result.IsVictory,
            Characters = party.OrderBy(entry => entry.Slot.SlotIndex).Select(entry => new
            {
                Actor = Actor("Character", entry.Character.Id), entry.Character.Hp,
                MaxHp = BattleActor.ForCharacter(entry).MaxHp
            }),
            Monsters = monsters.OrderBy(monster => monster.WaveNumber).ThenBy(monster => monster.Position)
                .Select(monster => new { Actor = Actor("Monster", monster.Id), monster.Hp, monster.MaxHp }),
            Events = result.Events.Select(fact => new
            {
                fact.Sequence, fact.RunSequence, fact.RoundNumber, Monster = Actor("Monster", fact.MonsterId),
                fact.Kind, fact.ActionKind, Source = Actor(fact.Source?.ActorType, fact.Source?.ActorId),
                Target = Actor(fact.Target.ActorType, fact.Target.ActorId), fact.SkillCode,
                fact.CalculatedAmount, fact.ActualAmount, fact.HpBefore, fact.HpAfter, fact.TargetMaxHp,
                fact.IsCritical, fact.Element, fact.ElementModifier, fact.StatusChange, fact.CountBefore,
                fact.CountAfter, Status = Status(fact.Status)
            }),
            Statuses = statuses.Where(state => state.RoomId == room.Id && state.RunSequence == room.RunSequence &&
                    state.ExpiresAfterRound >= room.RoundNumber)
                .Select(state => new
                {
                    Target = Actor(state.TargetType, state.TargetId), state.EffectCode, state.Stacks,
                    state.AppliedRound, state.ExpiresAfterRound, state.PerTickValue, state.MagnitudeSnapshot,
                    state.Lifetime, Source = Actor(state.SourceActorType, state.SourceActorId), state.SourceSkillCode,
                    BoundTarget = Actor(state.BoundTargetType, state.BoundTargetId)
                }).OrderBy(state => state.Target, StringComparer.Ordinal).ThenBy(state => state.EffectCode, StringComparer.Ordinal)
                .ThenBy(state => state.Source, StringComparer.Ordinal).ThenBy(state => state.BoundTarget, StringComparer.Ordinal),
            CharacterCooldowns = characterCooldowns.Where(state => state.RoomId == room.Id)
                .Select(state => new { Actor = Actor("Character", state.CharacterId), state.SkillCode, state.ReadyAtRound })
                .OrderBy(state => state.Actor, StringComparer.Ordinal).ThenBy(state => state.SkillCode, StringComparer.Ordinal),
            MonsterCooldowns = monsterCooldowns.Where(state => state.RoomId == room.Id)
                .Select(state => new { Actor = Actor("Monster", state.MonsterId), state.SkillCode, state.ReadyAtRound })
                .OrderBy(state => state.Actor, StringComparer.Ordinal).ThenBy(state => state.SkillCode, StringComparer.Ordinal),
            ConsumableCooldowns = (consumableCooldowns ?? []).Where(state => state.RoomId == room.Id)
                .Select(state => new { Actor = Actor("Character", state.CharacterId), state.CooldownGroup, state.ReadyAtRound })
                .OrderBy(state => state.Actor, StringComparer.Ordinal).ThenBy(state => state.CooldownGroup, StringComparer.Ordinal),
            HealingPotions = (healingPotions ?? []).Where(state => state.RoomId == room.Id && state.RunSequence == room.RunSequence)
                .Select(state => new { Actor = Actor("Character", state.CharacterId), state.UsesUsed })
                .OrderBy(state => state.Actor, StringComparer.Ordinal),
            OperationPotions = (operationPotions ?? []).Where(state => state.RoomId == room.Id && state.RunSequence == room.RunSequence)
                .Select(state => new { Actor = Actor("Character", state.CharacterId), state.ItemCode, state.AttackPercent,
                    state.FinalDamagePercent, state.DamageTakenPercent, state.NormalAttackDamagePercent, state.AreaDamageReductionPercent })
                .OrderBy(state => state.Actor, StringComparer.Ordinal),
            ConsumableBuffs = (consumableBuffs ?? []).Where(state => state.RoomId == room.Id &&
                    state.RunSequence == room.RunSequence && state.ExpiresAfterRound >= room.RoundNumber)
                .Select(state => new { Actor = Actor("Character", state.CharacterId), state.ItemCode, state.WeaponSkillCode,
                    state.SkillLevel, state.AppliedRound, state.ExpiresAfterRound })
                .OrderBy(state => state.Actor, StringComparer.Ordinal).ThenBy(state => state.ItemCode, StringComparer.Ordinal)
                .ThenBy(state => state.WeaponSkillCode, StringComparer.Ordinal)
        };
        var json = JsonSerializer.SerializeToElement(data);
        return new(json, Hash(json.GetRawText()));
    }

    public static string EncounterFingerprint(IEnumerable<BattleRoundTrace> rounds) =>
        Hash($"battle-trace:{SchemaVersion}\n" + string.Join("\n", rounds.Select(round => round.Fingerprint)));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
