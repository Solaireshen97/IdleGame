using Game.Shared.Dtos;
using Game.Shared;

namespace Game.Server.Services;

/// <summary>Isolates historical skill-node effects while stored node records and fixtures are migrated.</summary>
public sealed class LegacySkillTalentAdapter(ProfessionCastMechanic native, IReadOnlyDictionary<string, int> ranks,
    Func<int, string, int, Task> setState) : ProfessionCastMechanic
{
    public static bool AutoSelfCleanse(CharacterSkillDefinition skill, IReadOnlyDictionary<string, int> ranks) =>
        skill.Code == "rogue-evasion" && ranks.GetValueOrDefault("rogue-escape-artist") > 0;

    public override async Task PrepareAsync(BattleCastExecution cast, BattleEffectExecutor executor)
    {
        await native.PrepareAsync(cast, executor);
        if (cast.Skill.Code == "sword-parry" && ranks.GetValueOrDefault("sword-guard-stance") > 0) cast.LegacyGuardPower = 50;
    }

    public override BattleSkillEffect TransformEffect(BattleCastExecution cast, BattleSkillEffect effect, BattleEffectExecutor executor) =>
        native.TransformEffect(cast, effect, executor);
    public override IReadOnlyList<BattleActor> OrderTargets(BattleCastExecution cast, BattleSkillEffect effect, IReadOnlyList<BattleActor> targets) =>
        native.OrderTargets(cast, effect, targets);

    public override async Task AfterEffectAsync(BattleCastExecution cast, BattleSkillEffect effect, IReadOnlyList<BattleEffectOutcome> outcomes,
        BattleEffectExecutor executor)
    {
        await native.AfterEffectAsync(cast, effect, outcomes, executor);
        if (effect.Kind != BattleEffectKind.Interrupt || !outcomes.Any(outcome => outcome.Applied)) return;
        if (ranks.GetValueOrDefault("sword-disruption") > 0 && cast.Skill.Code == "sword-intercept")
            await setState(cast.Source.Id, "talent-intercept-echo", 3);
        if (ranks.GetValueOrDefault("rogue-opportunist") > 0 && cast.Skill.Code == "rogue-gouge" && cast.Battle.Monster.Hp > 0 &&
            executor.Statuses is { } statuses)
            await statuses.ApplyAsync(cast.Battle.Room, "Monster", cast.Battle.Monster.Id, "rogue-opening", 2,
                cast.Battle.Logs, cast.Battle.Monster.Name, source: cast.StatusSource);
    }

    public override async Task AfterCastAsync(BattleCastExecution cast, BattleEffectExecutor executor)
    {
        if (cast.Battle.Monster.Hp > 0 && cast.Skill.Code == "rogue-blade-flurry" && ranks.GetValueOrDefault("rogue-relentless-assault") > 0)
        {
            using var action = executor.Events.ActionScope(cast.Source, cast.Skill.Code, "夺命连攻", BattleActionKind.FollowUp);
            var hit = await executor.Damage.CharacterDamageAsync(cast.Battle, cast.Source, BattleSkillEffect.Damage(40, 1), BattleDamageOrigin.Skill,
                true, cast.HealthPercentAtCast, cast.ConditionalDamageBonus, cast.AttackPowerBonus, cast.DamageMultipliers);
            cast.Result.Outcomes.Add(new(BattleEffectKind.Damage, cast.Battle.Enemy, true, hit.CalculatedAmount, hit.ActualAmount, hit.IsCritical));
            cast.Battle.Logs.Add($"{cast.Source.Label} 触发 夺命连攻追加攻击 攻击 {cast.Battle.Monster.Name}，造成 {hit.CalculatedAmount} 点伤害{(hit.IsCritical ? "（暴击）" : "")}。");
        }
        if (executor.Statuses is { } statuses && AutoSelfCleanse(cast.CharacterSkill!, ranks))
        {
            var removed = await statuses.RemoveFirstAsync(cast.Battle.Room, "Character", [cast.Source.Id], false);
            if (removed is not null)
            {
                cast.Result.Outcomes.Add(new(BattleEffectKind.Cleanse, cast.Source, true, RemovedStatus: removed));
                cast.Battle.Logs.Add($"{cast.Source.Label} 借助 {cast.Skill.Name} 移除了 {removed.Name}。");
            }
        }
        await native.AfterCastAsync(cast, executor);
        if (!cast.Result.Applied || cast.Result.Damage <= 0) return;
        if (ranks.GetValueOrDefault("sword-rhythm") > 0) await setState(cast.Source.Id, "talent-sword-rhythm", 3);
        if (ranks.GetValueOrDefault("sword-assault-stance") > 0)
        {
            var restored = Math.Min((int)decimal.Floor(cast.Result.Damage * .10m), cast.Source.MaxHp - cast.Source.Hp);
            if (restored > 0)
            {
                var before = cast.Source.Hp;
                cast.Source.Hp += restored;
                executor.Events.Hp(cast.Battle.Room, BattleEventKind.Heal, cast.Source, cast.Source, restored, restored, before);
                cast.Battle.Logs.Add($"{cast.Source.Label} 从猛攻中恢复 {restored} 点生命值。");
            }
        }
    }
}
