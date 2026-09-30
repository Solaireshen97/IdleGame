using System.Collections.Immutable;

namespace Game.Server.Services;

public sealed record SkillBattleActor(int Id, int SlotIndex, int Hp, int MaxHp, bool HasRemovableDebuff)
{
    public bool IsAlive => Hp > 0;
}

public sealed record SkillBattleSnapshot(int CasterId, ImmutableArray<SkillBattleActor> Allies,
    int MonsterHp, int MonsterMaxHp, bool SupportsStatuses, bool MonsterHasRemovableBuff,
    bool CanInterrupt, bool MonsterHasInterruptibleSkill, ImmutableArray<string> DamageSkillsOnCooldown)
{
    public SkillBattleActor Caster => Allies.Single(actor => actor.Id == CasterId);
    public bool AdditionalSelfCleanse { get; init; }
}

public sealed record SkillAvailability(string? UnavailableReason, bool CanChooseAllyTarget,
    ImmutableArray<int> AllowedTargetCharacterIds)
{
    public bool CanUse => UnavailableReason is null;
}

/// <summary>Pure rules shared by queue validation, execution, Auto and the room projection.</summary>
public static class SkillBattlePolicy
{
    public static bool CanChooseAllyTarget(CharacterSkillDefinition skill) =>
        !skill.Effects.Any(effect => effect.TargetPolicy.Selection == BattleTargetSelection.AllAlive) &&
        skill.Effects.Any(effect => effect.TargetPolicy.AllowsSelection);

    public static ImmutableArray<SkillBattleActor> SelectAllies(BattleSkillEffect effect, SkillBattleSnapshot state,
        int? chosenTargetId = null) => SelectAllies(effect, state.Allies, state.CasterId, chosenTargetId);

    public static ImmutableArray<SkillBattleActor> SelectAllies(BattleSkillEffect effect, IEnumerable<SkillBattleActor> actors,
        int casterId, int? chosenTargetId = null)
    {
        var alive = actors.Where(actor => actor.IsAlive).OrderBy(actor => actor.SlotIndex).ToImmutableArray();
        if (chosenTargetId.HasValue && effect.TargetPolicy.AllowsSelection)
            return alive.Where(actor => actor.Id == chosenTargetId).ToImmutableArray();
        return effect.TargetPolicy.Selection switch
        {
            BattleTargetSelection.AllAlive or BattleTargetSelection.FirstDebuffed => alive,
            BattleTargetSelection.AllOtherAlive => alive.Where(actor => actor.Id != casterId).ToImmutableArray(),
            BattleTargetSelection.Self => alive.Where(actor => actor.Id == casterId).ToImmutableArray(),
            BattleTargetSelection.LowestHp => alive.OrderBy(actor => (decimal)actor.Hp / actor.MaxHp)
                .ThenBy(actor => actor.SlotIndex).Take(1).ToImmutableArray(),
            BattleTargetSelection.Front => alive.Take(1).ToImmutableArray(),
            _ => []
        };
    }

    public static SkillAvailability Availability(CharacterSkillDefinition skill, SkillBattleSnapshot state,
        int cooldownRemaining = 0, bool battleOver = false)
    {
        var canChoose = CanChooseAllyTarget(skill);
        var targets = canChoose ? state.Allies.Where(actor => actor.IsAlive &&
            HasApplicableEffect(skill, state, actor.Id)).OrderBy(actor => actor.SlotIndex).Select(actor => actor.Id).ToImmutableArray() : [];
        var reason = battleOver ? "BattleOver" : !state.Caster.IsAlive ? "CharacterDead" : cooldownRemaining > 0 ? "SkillCooldown" :
            HasApplicableEffect(skill, state) ? null : UnavailableEffectReason(skill);
        return new(reason, canChoose, targets);
    }

    public static bool HasApplicableEffect(CharacterSkillDefinition skill, SkillBattleSnapshot state, int? chosenTargetId = null)
    {
        if (chosenTargetId.HasValue && (!CanChooseAllyTarget(skill) ||
            !state.Allies.Any(actor => actor.Id == chosenTargetId && actor.IsAlive))) return false;
        if (skill.Effects.Any(effect => effect.Kind == BattleEffectKind.Interrupt) &&
            !skill.Effects.Any(effect => effect.Kind == BattleEffectKind.Damage)) return state.CanInterrupt;
        return skill.Effects.Any(effect => effect.Kind switch
        {
            BattleEffectKind.Damage => state.MonsterHp > 0,
            BattleEffectKind.Guard => SelectAllies(effect, state, chosenTargetId).Length > 0,
            BattleEffectKind.Heal => SelectAllies(effect, state, chosenTargetId).Any(actor => actor.Hp < actor.MaxHp),
            BattleEffectKind.Cleanse => state.SupportsStatuses && (SelectAllies(effect, state, chosenTargetId)
                .Any(actor => actor.HasRemovableDebuff) || state.AdditionalSelfCleanse && state.Caster.HasRemovableDebuff),
            BattleEffectKind.Dispel => state.SupportsStatuses && state.MonsterHasRemovableBuff,
            BattleEffectKind.Interrupt => state.CanInterrupt,
            BattleEffectKind.ApplyStatus => state.SupportsStatuses && (effect.TargetPolicy.Side == BattleTargetSide.Opponent
                ? state.MonsterHp > 0 : SelectAllies(effect, state, chosenTargetId).Length > 0),
            BattleEffectKind.CooldownReduction => state.DamageSkillsOnCooldown.Any(code => code != skill.Code),
            _ => false
        });
    }

    public static bool MeetsAutoCondition(CharacterSkillDefinition skill, SkillBattleSnapshot state, string? conditionOverride,
        int hpThresholdPercent, bool legacySelfCleanse = false)
    {
        if (conditionOverride is null && legacySelfCleanse && state.Caster.HasRemovableDebuff) return true;
        var alive = state.Allies.Where(actor => actor.IsAlive).OrderBy(actor => actor.SlotIndex).ToArray();
        bool HpMatches(SkillBattleActor actor) => (long)actor.Hp * 100 <= (long)actor.MaxHp * hpThresholdPercent;
        return (conditionOverride ?? skill.AutoCondition) switch
        {
            "Always" => true,
            "LowestHpBelowThreshold" => HpConditionTarget(skill, state) is { } target && HpMatches(target),
            "SelfHpBelowThreshold" => HpMatches(state.Caster),
            "AllyHpBelowThreshold" => alive.Any(HpMatches),
            "FrontAllyHpBelowThreshold" => alive.FirstOrDefault() is { } front && HpMatches(front),
            "MonsterHpBelowThreshold" => (long)state.MonsterHp * 100 <= (long)state.MonsterMaxHp * hpThresholdPercent,
            "AllyHasDebuff" => alive.Any(actor => actor.HasRemovableDebuff),
            "MonsterHasBuff" => state.MonsterHasRemovableBuff,
            "InterruptibleIntent" => state.CanInterrupt,
            "PreferInterrupt" => !state.MonsterHasInterruptibleSkill || state.CanInterrupt,
            _ => false
        };
    }

    private static SkillBattleActor? HpConditionTarget(CharacterSkillDefinition skill, SkillBattleSnapshot state)
    {
        var alive = state.Allies.Where(actor => actor.IsAlive).OrderBy(actor => actor.SlotIndex).ToArray();
        if (skill.Effects.Any(effect => effect.Kind == BattleEffectKind.Guard && effect.TargetPolicy.Side == BattleTargetSide.Self)) return state.Caster;
        if (skill.Effects.Any(effect => effect.Kind == BattleEffectKind.Guard)) return alive.FirstOrDefault();
        if (skill.Effects.Any(effect => effect.Kind == BattleEffectKind.Heal && effect.TargetPolicy.Side == BattleTargetSide.Self) &&
            !skill.Effects.Any(effect => effect.Kind == BattleEffectKind.Heal && effect.TargetPolicy.FixedAtCast &&
                effect.TargetPolicy.Selection == BattleTargetSelection.LowestHp)) return state.Caster;
        return alive.OrderBy(actor => (decimal)actor.Hp / actor.MaxHp).ThenBy(actor => actor.SlotIndex).FirstOrDefault();
    }

    private static string UnavailableEffectReason(CharacterSkillDefinition skill) =>
        skill.Effects.Any(effect => effect.Kind == BattleEffectKind.Cleanse) ? "NoRemovableDebuff" :
        skill.Effects.Any(effect => effect.Kind == BattleEffectKind.Dispel) ? "NoRemovableBuff" :
        skill.Effects.Any(effect => effect.Kind == BattleEffectKind.Interrupt) ? "NoInterruptibleIntent" :
        skill.Effects.Any(effect => effect.Kind == BattleEffectKind.Heal) ? "NoInjuredTarget" :
        skill.Effects.Any(effect => effect.Kind == BattleEffectKind.CooldownReduction) ? "NoReducibleCooldown" : "NoValidSkillTarget";
}
