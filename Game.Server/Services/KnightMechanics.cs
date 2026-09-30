using Game.Shared.Dtos;
using Game.Shared;

namespace Game.Server.Services;

public sealed class KnightMechanics(ProfessionMechanicCatalog? mechanics = null) : ProfessionCastMechanic
{
    private readonly KnightMechanicDefinition _rules = (mechanics ?? ProfessionMechanicCatalog.Default).Knight;
    public override Task PrepareAsync(BattleCastExecution cast, BattleEffectExecutor executor)
    {
        cast.GuardCounterEligible = string.Equals(cast.Source.Character?.ProfessionCode, "swordsman", StringComparison.OrdinalIgnoreCase) &&
            _rules.CanCounter(cast.Skill.Code);
        cast.DeduplicateHealing = _rules.DeduplicatesHealing(cast.Skill.Code);
        return Task.CompletedTask;
    }

    public static async Task ResolveCountersAsync(BattleExecutionContext battle, BattleGuardService guards, BattleDamageService damage,
        ProfessionMechanicCatalog? mechanics = null)
    {
        foreach (var participant in battle.Party.OrderBy(entry => entry.Slot.SlotIndex))
        {
            if (battle.Monster.Hp <= 0) break;
            var source = BattleActor.ForCharacter(participant);
            using var action = damage.Events.ActionScope(source, "knight-guard-counter", "守护反击", BattleActionKind.Counter);
            if (participant.Character.Hp <= 0 || !string.Equals(participant.Character.ProfessionCode, "swordsman", StringComparison.OrdinalIgnoreCase) ||
                !await guards.ConsumeCounterAsync(battle.Room, participant.Character.Id, battle.Monster.Id)) continue;
            var hit = await damage.CharacterDamageAsync(battle, source,
                BattleSkillEffect.Damage((mechanics ?? ProfessionMechanicCatalog.Default).Knight.CounterAttackPowerPercent), BattleDamageOrigin.Counter, false);
            battle.Logs.Add($"{source.Label} 守护反击 {battle.Monster.Name}，造成 {hit.CalculatedAmount} 点伤害。");
        }
    }
}
