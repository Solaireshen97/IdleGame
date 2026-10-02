using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;

namespace Game.Server.Services;

public sealed class MageMechanics(ProfessionMechanicCatalog? mechanics = null) : ProfessionCastMechanic
{
    private readonly ProfessionMechanicCatalog _mechanics = mechanics ?? ProfessionMechanicCatalog.Default;
    public override async Task AfterCastAsync(BattleCastExecution cast, BattleEffectExecutor executor)
    {
        if (!cast.Result.Applied || executor.Statuses is null) return;
        await AddDisorderAndEchoAsync(cast.Battle, cast.Source, executor, cast.Skill.Code, _mechanics);
        if (cast.Skill.Code == "mage-arcane-domain" && _mechanics.Mage.DomainCastAdditionalDisorderGain > 0)
            await AddDisorderAndEchoAsync(cast.Battle, cast.Source, executor,
            cast.Skill.Code, _mechanics, _mechanics.Mage.DomainCastAdditionalDisorderGain);
    }

    public static async Task RoundStartAsync(BattleExecutionContext battle, BattleEffectExecutor executor,
        ProfessionMechanicCatalog? mechanics = null)
    {
        var catalog = mechanics ?? ProfessionMechanicCatalog.Default;
        if (executor.Statuses is not { } statuses) return;
        foreach (var participant in battle.Party.Where(entry => entry.Character.Hp > 0 &&
            string.Equals(entry.Character.ProfessionCode, "mage", StringComparison.OrdinalIgnoreCase)))
        {
            if (battle.Monster.Hp <= 0) break;
            var source = BattleActor.ForCharacter(participant);
            var continuous = (await statuses.MechanicStatesAsync(battle.Room, "Character", source.Id, BattleStatusMechanic.ContinuousDispel))
                .Any(effect => effect.AppliedRound < battle.Room.RoundNumber);
            if (continuous)
            {
                using var action = executor.Events.ActionScope(source, "mage-spellbreak", "持续驱散", BattleActionKind.Mechanic);
                var removed = await statuses.RemoveFirstAsync(battle.Room, "Monster", [battle.Monster.Id], true);
                if (removed is not null)
                {
                    await executor.Damage.ObserveDispelAsync(battle, removed.Code);
                    battle.Logs.Add($"{source.Label} 的法术反制持续驱散了 {battle.Monster.Name} 的 {removed.Name}。");
                }
            }
            if (await DomainRankAsync(statuses, battle.Room, source.Id) > 0)
                await AddDisorderAndEchoAsync(battle, source, executor, "mage-arcane-domain", catalog);
            else await TryEchoAsync(battle, source, executor, catalog);
        }
    }

    private static async Task AddDisorderAndEchoAsync(BattleExecutionContext battle, BattleActor source,
        BattleEffectExecutor executor, string skillCode, ProfessionMechanicCatalog mechanics, int? gain = null)
    {
        if (battle.Monster.Hp <= 0 || executor.Statuses is not { } statuses) return;
        await AddDisorderAsync(statuses, battle.Room, source.Id, battle.Monster.Id, battle.Logs, source.Label, skillCode, mechanics, gain);
        await TryEchoAsync(battle, source, executor, mechanics, skillCode);
    }

    private static async Task TryEchoAsync(BattleExecutionContext battle, BattleActor source, BattleEffectExecutor executor,
        ProfessionMechanicCatalog mechanics, string? skillCode = null)
    {
        if (battle.Monster.Hp <= 0 || source.Hp <= 0 || executor.Statuses is not { } statuses ||
            (await statuses.MechanicStatesAsync(battle.Room, "Character", source.Id, BattleStatusMechanic.MageEchoUsed)).Count > 0 ||
            await DisorderStacksAsync(statuses, battle.Room, source.Id, battle.Monster.Id) < mechanics.Mage.EchoRequiredStacks) return;
        using var action = executor.Events.ActionScope(source, skillCode ?? "mage-disorder", "失序回响", BattleActionKind.Mechanic);
        await ConsumeDisorderAsync(statuses, battle.Room, source.Id, battle.Monster.Id, mechanics);
        var used = statuses.CatalogFor(battle.Room).FindMechanic(BattleStatusMechanic.MageEchoUsed)!;
        await statuses.SetCounterAsync(battle.Room, "Character", source.Id, used.Code, 1, new("Character", source.Id, skillCode));
        var rank = await DomainRankAsync(statuses, battle.Room, source.Id);
        var hit = await executor.Damage.CharacterDamageAsync(battle, source,
            BattleSkillEffect.Damage(rank > 0 ? mechanics.Mage.DomainEchoAttackPowerPercent : mechanics.Mage.EchoAttackPowerPercent), BattleDamageOrigin.Mechanic, false);
        battle.Logs.Add($"{source.Label} 触发失序回响，对 {battle.Monster.Name} 造成 {hit.CalculatedAmount} 点伤害。");
        if (battle.Monster.Hp > 0 && source.Hp > 0)
        {
            var disruption = mechanics.TryMageDisruptionStatus(statuses.CatalogFor(battle.Room), rank);
            if (disruption is not null) await statuses.ApplyAsync(battle.Room, "Monster", battle.Monster.Id, disruption.Code, 0,
                battle.Logs, battle.Monster.Name, source: new("Character", source.Id, skillCode));
        }
    }

    public static async Task<int> DisorderStacksAsync(BattleStatusService statuses, Room room, int id, int monsterId) =>
        (await statuses.MechanicStatesAsync(room, "Character", id, BattleStatusMechanic.MageDisorder))
            .FirstOrDefault(effect => effect.BoundTargetType == "Monster" && effect.BoundTargetId == monsterId)?.Stacks ?? 0;

    public static async Task<int> AddDisorderAsync(BattleStatusService statuses, Room room, int id, int monsterId, List<string> logs,
        string label, string? skillCode = null, ProfessionMechanicCatalog? mechanics = null, int? gain = null)
    {
        var definition = statuses.CatalogFor(room).FindMechanic(BattleStatusMechanic.MageDisorder);
        if (definition is null) return 0;
        var current = await DisorderStacksAsync(statuses, room, id, monsterId);
        var state = await statuses.SetCounterAsync(room, "Character", id, definition.Code,
            Math.Min(definition.MaxStacks, current + (gain ?? (mechanics ?? ProfessionMechanicCatalog.Default).Mage.DisorderGain)),
            new("Character", id, skillCode), "Monster", monsterId);
        logs.Add($"{label} 使当前怪物获得奥术失序（{state.Stacks} 层）。");
        return state.Stacks;
    }

    public static async Task<int> ConsumeDisorderAsync(BattleStatusService statuses, Room room, int id, int monsterId,
        ProfessionMechanicCatalog? mechanics = null)
    {
        var required = (mechanics ?? ProfessionMechanicCatalog.Default).Mage.EchoRequiredStacks;
        var current = await DisorderStacksAsync(statuses, room, id, monsterId);
        if (current < required) return 0;
        var definition = statuses.CatalogFor(room).FindMechanic(BattleStatusMechanic.MageDisorder)!;
        await statuses.ConsumeAsync(room, "Character", id, definition.Code, required);
        return current - required;
    }

    public static async Task<int> DomainRankAsync(BattleStatusService statuses, Room room, int id) =>
        (await statuses.MechanicStatesAsync(room, "Character", id, BattleStatusMechanic.MageDomain))
            .Select(effect => statuses.CatalogFor(room).Find(effect.EffectCode)!.MechanicLevel).DefaultIfEmpty(0).Max();
}
