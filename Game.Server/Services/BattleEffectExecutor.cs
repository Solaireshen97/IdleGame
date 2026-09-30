using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Dtos;

namespace Game.Server.Services;

/// <summary>Executes ordered effects for either side. Profession hooks add only their own mechanics.</summary>
public sealed class BattleEffectExecutor(SkillCatalog skills, BattleStatusService? statuses,
    BattleGuardService guards, BattleDamageService damage)
{
    public BattleStatusService? Statuses => statuses;
    public decimal? LegacyTalentValue(string code) => skills.LegacyTalentValue(code);
    public BattleDamageService Damage => damage;
    public BattleEventCollector Events => damage.Events;
    public BattleGuardService Guards => guards;

    public async Task<BattleSkillResult> ExecuteAsync(BattleCastExecution cast, ProfessionCastMechanic? mechanic = null)
    {
        using var action = Events.ActionScope(cast.Source, cast.Skill.Code, cast.Skill.Name,
            cast.IsBasicAttack ? BattleActionKind.NormalAttack : BattleActionKind.Skill);
        mechanic ??= new ProfessionCastMechanic();
        await mechanic.PrepareAsync(cast, this);
        if (cast.CharacterSkill is { } skill)
        {
            var requiredStatus = skill.RequiredTargetStatusCode is null || statuses is not null &&
                await statuses.HasAsync(cast.Battle.Room, "Monster", cast.Battle.Monster.Id, skill.RequiredTargetStatusCode);
            var hpCondition = skill.TargetHpBelowPercent is null ||
                (long)cast.Battle.Monster.Hp * 100 <= (long)cast.Battle.Monster.MaxHp * skill.TargetHpBelowPercent;
            cast.ConditionalDamageBonus = requiredStatus && hpCondition ? skill.ConditionalDamageBonusPercent : 0;
        }
        foreach (var original in cast.Skill.Effects)
        {
            var effect = mechanic.TransformEffect(cast, original, this);
            var targets = mechanic.OrderTargets(cast, effect, cast.SelectTargets(effect));
            var outcomes = await ExecuteEffectAsync(cast, effect, targets, mechanic);
            cast.Result.Outcomes.AddRange(outcomes);
            await mechanic.AfterEffectAsync(cast, effect, outcomes, this);
        }
        await mechanic.AfterCastAsync(cast, this);
        return cast.Result;
    }

    private async Task<List<BattleEffectOutcome>> ExecuteEffectAsync(BattleCastExecution cast, BattleSkillEffect effect,
        IReadOnlyList<BattleActor> targets, ProfessionCastMechanic mechanic)
    {
        var outcomes = new List<BattleEffectOutcome>();
        switch (effect.Kind)
        {
            case BattleEffectKind.Damage:
                foreach (var target in targets.Where(target => target.Hp > 0))
                {
                    if (cast.Source.Kind == BattleActorKind.Monster && target.Kind != BattleActorKind.Character) continue;
                    var hit = cast.Source.Kind == BattleActorKind.Character
                        ? await damage.CharacterDamageAsync(cast.Battle, cast.Source, effect, BattleDamageOrigin.Skill, true,
                            cast.HealthPercentAtCast, cast.ConditionalDamageBonus, cast.AttackPowerBonus, cast.DamageMultipliers)
                        : await damage.MonsterDamageAsync(cast.Battle, target, effect,
                            effect.TargetPolicy.Selection == BattleTargetSelection.AllAlive, cast.MonsterSkillReduction,
                            cast.LegacyIncomingReduction.GetValueOrDefault(target.Id));
                    cast.Battle.Logs.Add(cast.IsBasicAttack
                        ? $"{cast.Source.Label} 普通攻击 {target.Label}，造成 {hit.CalculatedAmount} 点伤害。"
                        : $"{cast.Source.Label} 使用 {cast.Skill.Name} 攻击 {target.Label}，造成 {hit.CalculatedAmount} 点伤害{(hit.IsCritical ? "（暴击）" : "")}。");
                    var outcome = new BattleEffectOutcome(effect.Kind, target, true, hit.CalculatedAmount, hit.ActualAmount, hit.IsCritical);
                    outcomes.Add(outcome);
                    await mechanic.AfterDamageTargetAsync(cast, outcome, this);
                }
                break;
            case BattleEffectKind.Heal:
                foreach (var target in targets.Where(target => cast.CanHeal(target)))
                {
                    var before = target.Hp;
                    var calculated = BattleDamageService.CalculateHealing(cast.Source, target, effect, cast.HealingMultiplier);
                    var restored = BattleDamageService.RestoreHp(target, calculated);
                    if (restored <= 0) continue;
                    Events.Hp(cast.Battle.Room, BattleEventKind.Heal, cast.Source, target, calculated, restored, before);
                    cast.Battle.Logs.Add($"{cast.Source.Label} 使用 {cast.Skill.Name}，为 {target.Label} 恢复 {restored} 点生命值。");
                    outcomes.Add(new(effect.Kind, target, true, calculated, restored));
                }
                break;
            case BattleEffectKind.Guard:
                foreach (var target in targets)
                {
                    var applied = await guards.ApplyForActorAsync(cast.Battle.Room, target.ActorType, target.Id,
                        cast.LegacyGuardPower > 0 ? cast.LegacyGuardPower : effect.Power, cast.StatusSource, cast.GuardCounterEligible);
                    if (applied) cast.Battle.Logs.Add($"{cast.Source.Label} 使用 {cast.Skill.Name}，守护 {target.Label}。");
                    outcomes.Add(new(effect.Kind, target, applied));
                }
                break;
            case BattleEffectKind.Cleanse when statuses is not null:
            case BattleEffectKind.Dispel when statuses is not null:
                foreach (var target in targets.Where(target => target.Hp > 0))
                {
                    var removed = await statuses.RemoveFirstAsync(cast.Battle.Room, target.ActorType,
                        [target.Id], effect.Kind == BattleEffectKind.Dispel);
                    if (removed is null) continue;
                    LogRemoval(cast, target, removed, effect.Kind);
                    outcomes.Add(new(effect.Kind, target, true, RemovedStatus: removed));
                    // FirstDebuffed searches the ordered group for one removable status;
                    // all-target effects instead remove one status from each selected actor.
                    if (effect.TargetPolicy.Selection == BattleTargetSelection.FirstDebuffed) break;
                }
                break;
            case BattleEffectKind.Interrupt:
                if (cast.Battle.InterruptIntent is not null && await cast.Battle.InterruptIntent())
                {
                    Events.Utility(cast.Battle.Room, BattleEventKind.Interrupt, cast.Battle.Enemy, label: "打断");
                    cast.Battle.Logs.Add($"{cast.Source.Label} 使用 {cast.Skill.Name}，打断了 {cast.Battle.Monster.Name} 的行动。");
                    outcomes.Add(new(effect.Kind, cast.Battle.Enemy, true));
                }
                break;
            case BattleEffectKind.ApplyStatus when statuses is not null && effect.StatusCode is not null:
                foreach (var target in targets.Where(target => target.Hp > 0))
                    outcomes.Add(await ApplyStatusAsync(cast, effect, target));
                break;
            case BattleEffectKind.CooldownReduction when cast.Source.Kind == BattleActorKind.Character:
                var affected = ReduceDamageCooldowns(cast, effect.Power, cast.Skill.Name);
                if (affected > 0) outcomes.Add(new(effect.Kind, cast.Source, true, affected, affected));
                break;
        }
        return outcomes;
    }

    public async Task<BattleEffectOutcome> ApplyStatusAsync(BattleCastExecution cast, BattleSkillEffect effect, BattleActor target)
    {
        var definition = statuses?.Catalog.Find(effect.StatusCode);
        if (definition is null) return new(BattleEffectKind.ApplyStatus, target, false);
        int? snapshot = null;
        if (definition.EffectType == "DamageOverTime" && effect.AttackPowerPercent > 0)
        {
            var attackModifier = await statuses!.ModifierAsync(cast.Battle.Room, cast.Source.ActorType, cast.Source.Id, "AttackPercent");
            var character = cast.Source.Character;
            var bonus = attackModifier + (character is null ? 0 : WeaponCombatRules.AttackBonusPercent(character, cast.Battle.Room.RoundNumber) +
                cast.Battle.OperationBonuses.GetValueOrDefault(character.Id).AttackPercent);
            var attack = character is null ? cast.Battle.Monster.Attack : TalentRules.EffectiveAttack(character);
            snapshot = Math.Max(1, (int)Math.Min(int.MaxValue, decimal.Floor(attack *
                Math.Max(0, 1m + bonus / 100m) * effect.AttackPowerPercent / 100m)));
        }
        else if (definition.EffectType == "HealOverTime" && effect.HealMaxHpPercent > 0)
            snapshot = Math.Max(1, (int)decimal.Floor(target.MaxHp * effect.HealMaxHpPercent / 100m));
        var bindsEnemy = definition.Mechanic == BattleStatusMechanic.HunterMark;
        if (bindsEnemy && cast.Battle.Monster.Hp <= 0) return new(BattleEffectKind.ApplyStatus, target, false);
        var applied = await statuses!.ApplyAsync(cast.Battle.Room, target.ActorType, target.Id, definition.Code,
            effect.DurationRounds, bindsEnemy ? [] : cast.Battle.Logs, target.Label, snapshot, cast.StatusSource,
            bindsEnemy ? "Monster" : null, bindsEnemy ? cast.Battle.Monster.Id : null);
        if (bindsEnemy && applied) cast.Battle.Logs.Add($"{target.Label} 标记了当前猎物，持续 {effect.DurationRounds + 1} 回合。");
        return new(BattleEffectKind.ApplyStatus, target, applied, StatusCode: definition.Code);
    }

    public async Task<BattleEffectOutcome?> CleanseAsync(BattleCastExecution cast, BattleActor target)
    {
        if (statuses is null) return null;
        var removed = await statuses.RemoveFirstAsync(cast.Battle.Room, target.ActorType, [target.Id], false);
        if (removed is null) return null;
        LogRemoval(cast, target, removed, BattleEffectKind.Cleanse);
        var outcome = new BattleEffectOutcome(BattleEffectKind.Cleanse, target, true, RemovedStatus: removed);
        cast.Result.Outcomes.Add(outcome);
        return outcome;
    }

    private static void LogRemoval(BattleCastExecution cast, BattleActor target, RemovedBattleStatus removed, BattleEffectKind kind) =>
        cast.Battle.Logs.Add(kind == BattleEffectKind.Cleanse
            ? $"{cast.Source.Label} 使用 {cast.Skill.Name}，移除了 {target.Label} 的 {removed.Name}。"
            : $"{cast.Source.Label} 使用 {cast.Skill.Name}，驱散了 {target.Label} 的 {removed.Name}。");

    public int ReduceDamageCooldowns(BattleCastExecution cast, int rounds, string sourceName)
    {
        if (cast.Source.Character is not { } character) return 0;
        var affected = 0;
        foreach (var entry in cast.Cooldowns.Where(entry => entry.CharacterId == character.Id && entry.SkillCode != cast.Skill.Code &&
            entry.ReadyAtRound > cast.Battle.Room.RoundNumber && !entry.SkillCode.StartsWith(SoulImprintRules.CooldownPrefix) &&
            skills.Resolve(character, entry.SkillCode, cast.ProfessionLevels)?.Effects.Any(effect => effect.Kind == BattleEffectKind.Damage) == true))
        {
            entry.ReadyAtRound = Math.Max(cast.Battle.Room.RoundNumber, entry.ReadyAtRound - rounds);
            affected++;
        }
        if (affected > 0) Events.Utility(cast.Battle.Room, BattleEventKind.Cooldown, cast.Source, rounds, $"冷却缩短 {rounds} 回合");
        if (affected > 0) cast.Battle.Logs.Add($"{cast.Source.Label} 的 {sourceName} 使 {affected} 个伤害技能的冷却缩短 {rounds} 回合。");
        return affected;
    }
}
