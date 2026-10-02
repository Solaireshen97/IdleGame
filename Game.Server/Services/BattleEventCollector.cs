using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;

namespace Game.Server.Services;

/// <summary>Records only the current round. The caller publishes its frozen facts after the database commits.</summary>
public sealed class BattleEventCollector
{
    private sealed record Recording(int RoomId, int Run, int Round, int MonsterId,
        Dictionary<(string, int), BattleEventActor> Actors, List<BattleEventResponse> Events);
    private sealed record Action(BattleEventActor? Source, string? Code, string Label, BattleActionKind Kind);
    private Recording? _recording;
    private Action? _action;
    public bool IsRecording => _recording is not null;

    public IDisposable Begin(Room room, Monster monster, IReadOnlyList<BattleParticipant> party)
    {
        if (_recording is not null) throw new InvalidOperationException("A battle recording is already active.");
        var actors = party.Select(BattleActor.ForCharacter).Append(BattleActor.ForMonster(monster))
            .ToDictionary(actor => (actor.ActorType, actor.Id), Reference);
        var current = new Recording(room.Id, room.RunSequence, room.RoundNumber, monster.Id, actors, []);
        _recording = current;
        return new Scope(() => { if (_recording == current) { _recording = null; _action = null; } });
    }

    public IDisposable ActionScope(BattleActor? source, string? code, string label, BattleActionKind kind) =>
        ActionScope(source is null ? null : Reference(source), code, label, kind);

    public IDisposable ActionScope(BattleStatusSource? source, string label, BattleActionKind kind) =>
        ActionScope(source is { } actor ? Resolve(actor.ActorType, actor.ActorId) : null, source?.SkillCode, label, kind);

    private IDisposable ActionScope(BattleEventActor? source, string? code, string label, BattleActionKind kind)
    {
        var previous = _action;
        _action = new(source, code, label, kind);
        return new Scope(() => _action = previous);
    }

    public string? ActorName(string? type, int? id) => type is not null && id.HasValue ? Resolve(type, id.Value).Name : null;
    private BattleEventActor Resolve(string type, int id) => _recording?.Actors.GetValueOrDefault((type, id)) ?? new(type, id);
    private static BattleEventActor Reference(BattleActor actor) => new(actor.ActorType, actor.Id,
        actor.Character is null ? null : actor.SlotIndex, actor.Name, actor.Character?.ProfessionCode);

    private void Add(Room room, BattleEventResponse fact)
    {
        if (_recording is not { } recording || recording.RoomId != room.Id || recording.Run != room.RunSequence ||
            recording.Round != room.RoundNumber) return;
        recording.Events.Add(fact with
        {
            Sequence = recording.Events.Count + 1, RoomId = recording.RoomId, RunSequence = recording.Run,
            RoundNumber = recording.Round + 1, MonsterId = recording.MonsterId,
            ActionKind = _action?.Kind ?? fact.ActionKind, SkillCode = _action?.Code ?? fact.SkillCode,
            // A removal's owner is the cleanser/dispeller, never the original status provider.
            Source = fact.Kind is BattleEventKind.Cleanse or BattleEventKind.Dispel ? _action?.Source : _action?.Source ?? fact.Source,
            Label = string.IsNullOrEmpty(fact.Label) ? _action?.Label ?? "" : fact.Label
        });
    }

    public void Hp(Room room, BattleEventKind kind, BattleActor? source, BattleActor target, int calculated, int actual,
        int before, bool critical = false, ElementType? element = null, decimal modifier = 0)
    {
        if (actual <= 0) return;
        Add(room, new()
        {
            Kind = kind, Source = source is null ? null : Reference(source), Target = Reference(target),
            CalculatedAmount = calculated, ActualAmount = actual, HpBefore = before, HpAfter = target.Hp, TargetMaxHp = target.MaxHp,
            IsCritical = critical, Element = element, ElementModifier = modifier
        });
        if (kind == BattleEventKind.Damage && target.Kind == BattleActorKind.Monster && target.Hp == 0)
            Add(room, new() { Kind = BattleEventKind.Defeat, Source = source is null ? null : Reference(source),
                Target = Reference(target), HpBefore = before, HpAfter = 0, Label = "目标已击败" });
    }

    public void Utility(Room room, BattleEventKind kind, BattleActor target, int amount = 0, string label = "") =>
        Add(room, new() { Kind = kind, Target = Reference(target), ActualAmount = amount, CalculatedAmount = amount, Label = label });

    public void Status(Room room, string ownerType, int ownerId, BattleStatusSnapshot snapshot,
        BattleStatusChange change, int before, int after, BattleEventKind kind = BattleEventKind.Status) => Add(room, new()
    {
        Kind = kind, Target = Resolve(ownerType, ownerId), Status = snapshot, StatusChange = change,
        CountBefore = before, CountAfter = after, Label = snapshot.Name,
        Source = snapshot.SourceActorType is { } type && snapshot.SourceActorId is { } id ? Resolve(type, id) : null,
        SkillCode = snapshot.SourceSkillCode
    });

    public List<BattleEventResponse> Snapshot(Room room) => _recording is { } recording && recording.RoomId == room.Id &&
        recording.Run == room.RunSequence ? recording.Events.Select(fact => fact with { SettlementVersion = room.Version }).ToList() : [];

    private sealed class Scope(System.Action release) : IDisposable
    {
        private System.Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
