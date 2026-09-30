using Game.Shared;
using Game.Shared.Models;

namespace Game.Server.Services;

/// <summary>Guard strength, ownership and pending counters are real statuses in the battle transaction.</summary>
public sealed class BattleGuardService(BattleStatusService statuses)
{
    public const string GuardCode = "battle-round-guard";
    public const string PermissionCode = "battle-guard-counter-permission";
    public const string ReadyCode = "battle-guard-counter-ready";

    public async Task<bool> ApplyAsync(Room room, int targetId, int reductionPercent, BattleStatusSource? source,
        bool counterEligible) => await ApplyForActorAsync(room, "Character", targetId, reductionPercent, source, counterEligible);

    public async Task<bool> ApplyForActorAsync(Room room, string actorType, int targetId, int reductionPercent,
        BattleStatusSource? source, bool counterEligible = false)
    {
        if (reductionPercent <= 0) return false;
        var power = Math.Min(BattleRules.MaxGuardDamageReductionPercent, reductionPercent);
        var current = await DefenseAsync(room, actorType, targetId);
        // Equal strength keeps the original provider and their counter eligibility.
        if (current.ReductionPercent >= power) return false;
        await statuses.ApplyAsync(room, actorType, targetId, GuardCode, 0, [], string.Empty,
            source: source, magnitudeSnapshot: power);
        await statuses.RemoveAsync(room, actorType, targetId, PermissionCode);
        if (actorType == "Character" && counterEligible && source is { ActorType: "Character" })
            await statuses.SetCounterAsync(room, "Character", targetId, PermissionCode, 1, source);
        return true;
    }

    public Task<CharacterRoundDefense> DefenseAsync(Room room, int targetId) => DefenseAsync(room, "Character", targetId);

    public async Task<CharacterRoundDefense> DefenseAsync(Room room, string actorType, int targetId)
    {
        var effects = await statuses.GetActiveAsync(room, actorType, [targetId]);
        var guard = effects.FirstOrDefault(effect => effect.EffectCode == GuardCode);
        if (guard is null) return default;
        var sourceId = guard.SourceActorType == "Character" ? guard.SourceActorId : null;
        var permission = effects.Any(effect => effect.EffectCode == PermissionCode && effect.SourceActorId == sourceId &&
            effect.SourceActorType == "Character");
        return new((int)(guard.MagnitudeSnapshot ?? 0), sourceId, permission);
    }

    public async Task RecordHitAsync(Room room, int targetId, int monsterId)
    {
        var permission = (await statuses.GetActiveAsync(room, "Character", [targetId]))
            .FirstOrDefault(effect => effect.EffectCode == PermissionCode);
        if (permission?.SourceActorId is not int knightId || permission.SourceActorType != "Character") return;
        var source = new BattleStatusSource("Character", knightId, permission.SourceSkillCode);
        await statuses.ConsumeAsync(room, "Character", targetId, PermissionCode);
        await statuses.SetCounterAsync(room, "Character", knightId, ReadyCode, 1, source, "Monster", monsterId);
    }

    public async Task<bool> ConsumeCounterAsync(Room room, int characterId, int monsterId)
    {
        var ready = (await statuses.GetActiveAsync(room, "Character", [characterId])).Any(effect =>
            effect.EffectCode == ReadyCode && effect.BoundTargetType == "Monster" && effect.BoundTargetId == monsterId);
        return ready && await statuses.ConsumeAsync(room, "Character", characterId, ReadyCode) > 0;
    }

    public Task ClearRoundAsync(Room room, IEnumerable<int> characterIds) =>
        statuses.RemoveCodesAsync(room, "Character", characterIds.ToArray(), [GuardCode, PermissionCode, ReadyCode]);
}

public readonly record struct CharacterRoundDefense(int ReductionPercent, int? SourceCharacterId = null,
    bool KnightCounterEligible = false);
