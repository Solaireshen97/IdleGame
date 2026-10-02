using Game.Shared.Dtos;
using Game.Shared.Models;

namespace Game.Server.Services;

public sealed partial class BattleDamageService
{
    // Pending damage lives only inside one synchronous combat action. It is resolved before the round commits;
    // the actual round budget/HP snapshot and attribute-hit deduplication are persistent statuses.
    private ReflectionAction? _reflectionAction;

    public ReflectionAction BeginReflectionAction(BattleExecutionContext battle, BattleActor source)
    {
        var action = new ReflectionAction(this, _reflectionAction, battle, source);
        _reflectionAction = action;
        return action;
    }

    public sealed class ReflectionAction : IDisposable
    {
        private readonly BattleDamageService _owner;
        private readonly ReflectionAction? _previous;
        private readonly BattleExecutionContext _battle;
        internal BattleActor Source { get; }
        internal decimal Raw { get; set; }
        private bool _completed;
        internal ReflectionAction(BattleDamageService owner, ReflectionAction? previous,
            BattleExecutionContext battle, BattleActor source) => (_owner, _previous, _battle, Source) = (owner, previous, battle, source);
        public async Task CompleteAsync()
        {
            if (_completed) return;
            _completed = true;
            await _owner.ApplyReflectionAsync(_battle, Source, Raw);
        }
        public void Dispose() { if (_owner._reflectionAction == this) _owner._reflectionAction = _previous; }
    }

    private async Task ReflectCharacterDamageAsync(BattleExecutionContext battle, BattleActor source, int damage)
    {
        if (phases is null || damage <= 0 || source.Hp <= 0) return;
        var raw = await phases.ReflectionMirrorRawAsync(battle, damage);
        if (raw <= 0) return;
        if (_reflectionAction is { } action && action.Source.Kind == source.Kind && action.Source.Id == source.Id)
            action.Raw += raw;
        else await ApplyReflectionAsync(battle, source, raw);
    }

    private async Task ApplyReflectionAsync(BattleExecutionContext battle, BattleActor source, decimal raw)
    {
        if (phases is null || statuses is null || raw <= 0 || source.Hp <= 0 || battle.Monster.Hp <= 0) return;
        var amount = await phases.ConsumeReflectionMirrorBudgetAsync(battle, source, raw);
        if (amount <= 0) return;
        var guard = await guards.DefenseAsync(battle.Room, source.Id);
        var reduction = await statuses.ModifierAsync(battle.Room, "Character", source.Id, "ReductionPercent");
        var potion = battle.OperationBonuses.GetValueOrDefault(source.Id);
        var combined = WeaponCombatRules.CombinedDirectReductionPercent(guard.ReductionPercent + reduction -
            potion.DamageTakenPercent, battle.StatsFor(source.Character!), source.Hp);
        // Derived reflection never rolls damage variance, element advantage, criticals, counter permission or leech.
        var damage = (int)decimal.Floor(amount * Math.Max(0, 1m - combined / 100m));
        var before = source.Hp;
        source.Hp = Math.Max(0, before - damage);
        using var action = Events.ActionScope(battle.Enemy, "light-reflection", "琉辉反镜反射", BattleActionKind.Mechanic);
        Events.Hp(battle.Room, BattleEventKind.Damage, battle.Enemy, source, damage, before - source.Hp, before);
        if (before > source.Hp) battle.Logs.Add($"{source.Label} 受到琉辉反镜反射，损失{before - source.Hp}点生命。");
    }
}
