using Game.Shared.Dtos;
using Game.Shared.Enums;

namespace Game.Client.Services;

// Maps committed facts to presentation. Combat text and actor names never determine behavior.
public static class BattleFeedbackPlanner
{
    public static bool SameEncounter(RoomDetailResponse a, RoomDetailResponse b) =>
        a.RoomId == b.RoomId && a.RunSequence == b.RunSequence && a.MonsterId == b.MonsterId &&
        a.CurrentWaveNumber == b.CurrentWaveNumber && a.CurrentEnemyNumber == b.CurrentEnemyNumber;

    public static BattleFeedbackPlan? Create(RoomDetailResponse? before, RoomDetailResponse after)
    {
        if (before is null || before.ClosedAtUtc.HasValue || before.RoomId != after.RoomId ||
            before.RunSequence != after.RunSequence || before.MonsterHp <= 0 || before.MonsterId <= 0 ||
            after.RoundNumber != before.RoundNumber + 1) return null;
        var facts = after.BattleEvents.Where(fact => fact.RoomId == before.RoomId &&
            fact.RunSequence == before.RunSequence && fact.RoundNumber == after.RoundNumber &&
            fact.MonsterId == before.MonsterId && fact.SettlementVersion <= after.RoomVersion).OrderBy(fact => fact.Sequence).ThenBy(fact => fact.Id).ToList();
        var sameEnemy = SameEncounter(before, after);
        var defeated = facts.Any(fact => fact.Kind == BattleEventKind.Defeat &&
            fact.Target.ActorType == "Monster" && fact.Target.ActorId == before.MonsterId);
        if (!sameEnemy && !defeated || after.ClosedAtUtc.HasValue && !defeated) return null;
        var events = new List<BattleFeedbackEvent>();
        foreach (var fact in facts)
        {
            if (Map(fact, before) is not { } current) continue;
            // Only the declared normal echo can share the immediately preceding swing.
            var previous = events.LastOrDefault();
            if (fact.ActionKind == BattleActionKind.FollowUp && fact.SkillCode == "normal-echo")
                current = current with { IsFollowUp = previous is { Kind: "damage", ActionKind: BattleActionKind.NormalAttack } &&
                    previous.Source.Length > 0 && previous.Source == current.Source && previous.Target == current.Target };
            // A queued skill has one windup per actor and round, even when
            // counter/status facts interleave its targets. Never merge its hits.
            if (current.IsSkill && current.Source.Length > 0 && current.SkillCode is not null)
                current = current with { CastKey = $"{after.RoundNumber}:{current.Source}:{(int)current.ActionKind}:{current.SkillCode}" };
            events.Add(current);
        }
        if (events.Count == 0) return null;
        var vitals = after.Slots.Where(slot => slot.IsOccupied).ToDictionary(
            slot => slot.SlotIndex.ToString(), slot => new BattleFeedbackVitals(slot.CharacterHp ?? 0, slot.CharacterMaxHp ?? 1));
        vitals["enemy"] = new(defeated ? 0 : after.MonsterHp, before.MonsterMaxHp);
        var statuses = before.Slots.Where(slot => slot.IsOccupied).ToDictionary(slot => slot.SlotIndex.ToString(),
            slot => slot.StatusEffects.Select(BattleFeedbackStatus.From).ToList());
        statuses["enemy"] = before.MonsterEffects.Select(BattleFeedbackStatus.From).ToList();
        return new(after.RoundNumber, events, vitals, defeated,
            sameEnemy && after.RoomStatus == RoomStatus.BattleOver && defeated, statuses);
    }

    public static bool CanFinishPlayback(RoomDetailResponse scene, RoomDetailResponse latest, BattleFeedbackPlan plan) =>
        scene.RoomId == latest.RoomId && scene.RunSequence == latest.RunSequence && latest.RoundNumber == plan.Round &&
        (plan.Defeated || !latest.ClosedAtUtc.HasValue && SameEncounter(scene, latest));

    private static string Key(BattleEventActor? actor, RoomDetailResponse scene) => actor switch
    {
        { ActorType: "Monster" } when actor.ActorId == scene.MonsterId => "enemy",
        { ActorType: "Character" } => scene.Slots.FirstOrDefault(slot => slot.IsOccupied && slot.CharacterId == actor.ActorId)?.SlotIndex.ToString() ?? "",
        _ => ""
    };

    private static BattleFeedbackEvent? Map(BattleEventResponse fact, RoomDetailResponse scene)
    {
        var target = Key(fact.Target, scene);
        if (target.Length == 0 || fact.Kind == BattleEventKind.Defeat) return null;
        var source = Key(fact.Source, scene);
        var numeric = fact.Kind is BattleEventKind.Damage or BattleEventKind.Heal;
        if (numeric && fact.ActualAmount <= 0) return null;
        var status = fact.Status is { } snapshot ? BattleFeedbackStatus.From(snapshot.ToResponse()) : null;
        var kind = fact.Kind switch
        {
            BattleEventKind.Damage => "damage", BattleEventKind.Heal => "heal",
            BattleEventKind.Cleanse => "cleanse", BattleEventKind.Dispel => "dispel",
            BattleEventKind.Interrupt => "interrupt", BattleEventKind.Cooldown => "cooldown",
            BattleEventKind.Status when fact.StatusChange is BattleStatusChange.Removed or BattleStatusChange.Expired => "status",
            BattleEventKind.Status when status is { IsPositive: true } => "buff",
            BattleEventKind.Status when status is { IsPositive: false } => "debuff",
            _ => "status"
        };
        var tone = fact.Kind == BattleEventKind.Damage ? fact.Element?.ToString().ToLowerInvariant() ?? "neutral" : kind switch
        { "heal" or "cleanse" => "heal", "buff" or "dispel" or "interrupt" or "cooldown" => "light", "debuff" => "dark", _ => "neutral" };
        var style = !numeric || fact.Kind == BattleEventKind.Heal || source.Length == 0 || fact.ActionKind == BattleActionKind.Periodic
            ? "pulse" : fact.ActionKind == BattleActionKind.SoulImprint ? "magic" : fact.Source?.ProfessionCode?.ToUpperInvariant() switch
            { "MAGE" => "magic", "ACOLYTE" or "CLERIC" => "holy", "HUNTER" => "arrow", "ROGUE" => "dagger", _ => "slash" };
        var suffix = fact.StatusChange switch
        { BattleStatusChange.Consumed => " · 消耗", BattleStatusChange.Removed => " · 移除", BattleStatusChange.Expired => " · 到期", BattleStatusChange.Retained => " · 保留", _ => "" };
        return new(source, target, numeric ? fact.ActualAmount : 0, kind, style, tone, fact.Label + suffix,
            fact.Kind == BattleEventKind.Damage && fact.IsCritical, (int)decimal.Round(fact.ElementModifier),
            ActionKind: fact.ActionKind, IsSkill: fact.ActionKind is BattleActionKind.Skill or BattleActionKind.SoulImprint &&
                (numeric || fact.Kind is BattleEventKind.Cleanse or BattleEventKind.Dispel or BattleEventKind.Interrupt or BattleEventKind.Cooldown ||
                    fact.Kind == BattleEventKind.Status && fact.StatusChange is BattleStatusChange.Added or BattleStatusChange.Refreshed),
            HpAfter: fact.HpAfter, TargetMaxHp: fact.TargetMaxHp, Status: status, StatusChange: fact.StatusChange?.ToString(), CountAfter: fact.CountAfter,
            SkillCode: fact.SkillCode, ProfessionCode: fact.Source?.ProfessionCode, StatusEffectType: fact.Status?.EffectType);
    }
}

public sealed record BattleFeedbackStatus(string Code, string Name, string Description, bool IsPositive,
    int Stacks, string DurationText, string CounterText, string Glyph, string? BoundTargetName)
{
    public static BattleFeedbackStatus From(BattleStatusEffectResponse effect) => new(effect.Code, effect.Name,
        effect.Description, effect.IsPositive, effect.Stacks, effect.DurationText, effect.CounterText,
        BattleStatusPresentation.Glyph(effect), effect.BoundTargetName);
}

public sealed record BattleFeedbackEvent(string Source, string Target, int Amount, string Kind,
    string Style, string Tone, string Label, bool Critical, int ElementModifierPercent = 0, bool IsFollowUp = false,
    BattleActionKind ActionKind = BattleActionKind.Skill, bool IsSkill = false, int? HpAfter = null, int? TargetMaxHp = null,
    BattleFeedbackStatus? Status = null, string? StatusChange = null, int? CountAfter = null,
    string? SkillCode = null, string? ProfessionCode = null, string? StatusEffectType = null, string? CastKey = null);
public sealed record BattleFeedbackVitals(int Hp, int MaxHp);
public sealed record BattleFeedbackPlan(int Round, List<BattleFeedbackEvent> Events,
    Dictionary<string, BattleFeedbackVitals> FinalVitals, bool Defeated, bool Victory,
    Dictionary<string, List<BattleFeedbackStatus>> InitialStatuses)
{
    public long TotalDamage => Events.Where(e => e.Target == "enemy" && e.Kind == "damage").Sum(e => (long)e.Amount);
    public int HitCount => Events.Count(e => e.Target == "enemy" && e.Kind == "damage");
    public int CriticalCount => Events.Count(e => e.Critical);
}
