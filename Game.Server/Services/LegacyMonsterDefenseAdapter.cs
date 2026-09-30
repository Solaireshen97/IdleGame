using Game.Shared.Dtos;
using Game.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class LegacyMonsterDefenseAdapter(GameDbContext db, BattleGuardService guards) : ProfessionCastMechanic
{
    private Dictionary<int, List<string>> _nodes = [];
    public override async Task PrepareAsync(BattleCastExecution cast, BattleEffectExecutor executor)
    {
        var ids = cast.Battle.Party.Select(entry => entry.Character.Id).ToArray();
        var nodes = await db.CharacterSkillTalents.Where(node => ids.Contains(node.CharacterId) &&
            (node.NodeCode == "sword-guard-stance" || node.NodeCode == "sword-counteroffense")).ToListAsync();
        _nodes = nodes.GroupBy(node => node.CharacterId).ToDictionary(group => group.Key, group => group.Select(node => node.NodeCode).ToList());
        foreach (var (id, values) in _nodes)
            if (values.Contains("sword-guard-stance", StringComparer.OrdinalIgnoreCase)) cast.LegacyIncomingReduction[id] = 10;
    }

    public override async Task AfterDamageTargetAsync(BattleCastExecution cast, BattleEffectOutcome outcome,
        BattleEffectExecutor executor)
    {
        var nodes = _nodes.GetValueOrDefault(outcome.Target.Id);
        if (nodes is null || cast.Battle.Monster.Hp <= 0) return;
        var guard = await guards.DefenseAsync(cast.Battle.Room, outcome.Target.Id);
        if (guard.ReductionPercent <= 0 || guard.SourceCharacterId != outcome.Target.Id) return;
        if (nodes.Contains("sword-guard-stance", StringComparer.OrdinalIgnoreCase))
        {
            using var action = executor.Events.ActionScope(outcome.Target, "legacy-parry", "招架反击", BattleActionKind.Counter);
            var hit = await executor.Damage.CharacterDamageAsync(cast.Battle, outcome.Target, BattleSkillEffect.Damage(50), BattleDamageOrigin.LegacyParry, false);
            cast.Battle.Logs.Add($"{outcome.Target.Label} 招架后反击 {cast.Battle.Monster.Name}，造成 {hit.CalculatedAmount} 点伤害。");
        }
        if (nodes.Contains("sword-counteroffense", StringComparer.OrdinalIgnoreCase))
        {
            using var action = executor.Events.ActionScope(outcome.Target, "sword-counteroffense", "反攻蓄势", BattleActionKind.Mechanic);
            if (executor.Statuses is { } statuses)
                await statuses.ApplyAsync(cast.Battle.Room, "Character", outcome.Target.Id, "talent-guard-echo", 3, [], string.Empty,
                    source: new("Character", outcome.Target.Id, "sword-counteroffense"),
                    magnitudeSnapshot: executor.LegacyTalentValue("sword-counteroffense") ?? 75, counterCount: 1);
        }
    }
}
