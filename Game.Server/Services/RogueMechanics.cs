using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;

namespace Game.Server.Services;

public sealed class RogueMechanics(ProfessionMechanicCatalog? mechanics = null) : ProfessionCastMechanic
{
    private readonly ProfessionMechanicCatalog _mechanics = mechanics ?? ProfessionMechanicCatalog.Default;
    public override async Task PrepareAsync(BattleCastExecution cast, BattleEffectExecutor executor)
    {
        if (cast.Skill.Code != "rogue-execution-slash") return;
        cast.Result.UseActualDamage = true;
        var definition = executor.Statuses?.Catalog.FindMechanic(BattleStatusMechanic.ShadowCharges);
        var charges = definition is null ? 0 : await executor.Statuses!.ConsumeAsync(cast.Battle.Room, "Character", cast.Source.Id, definition.Code);
        if (charges <= 0) return;
        cast.DamageMultipliers.Add(1 + charges * _mechanics.Rogue.DamageBonusPerChargePercent / 100m);
        cast.Battle.Logs.Add($"{cast.Source.Label} 的斩击消耗 {charges} 层影之蓄势，伤害提高 {charges * _mechanics.Rogue.DamageBonusPerChargePercent}%。");
    }

    public override Task AfterEffectAsync(BattleCastExecution cast, BattleSkillEffect effect, IReadOnlyList<BattleEffectOutcome> outcomes,
        BattleEffectExecutor executor)
    {
        if (cast.Skill.Code != "rogue-execution-slash" || effect.Kind != BattleEffectKind.Damage) return Task.CompletedTask;
        foreach (var outcome in outcomes.Where(outcome => outcome.ActualAmount > 0))
        {
            var monster = cast.Battle.Monster;
            if (monster.Hp <= 0 || (long)monster.Hp * 100 >= (long)monster.MaxHp * _mechanics.Rogue.ExecuteBelowHpPercent) continue;
            var followUp = Math.Min(monster.Hp, (int)decimal.Floor(outcome.ActualAmount * _mechanics.Rogue.ExecuteActualDamagePercent / 100m));
            if (followUp <= 0) continue;
            var before = monster.Hp;
            monster.Hp -= followUp;
            using var action = executor.Events.ActionScope(cast.Source, cast.Skill.Code, "斩杀追击", BattleActionKind.FollowUp);
            executor.Events.Hp(cast.Battle.Room, BattleEventKind.Damage, cast.Source, cast.Battle.Enemy, followUp, followUp, before);
            cast.Result.Outcomes.Add(new(BattleEffectKind.Damage, cast.Battle.Enemy, true, followUp, followUp));
            cast.Battle.Logs.Add($"{cast.Source.Label} 的斩杀追击对 {monster.Name} 造成 {followUp} 点伤害。");
        }
        return Task.CompletedTask;
    }

    public override async Task AfterCastAsync(BattleCastExecution cast, BattleEffectExecutor executor)
    {
        if (!cast.Result.Applied || cast.CharacterSkill?.IsShared != false || executor.Statuses is null ||
            !_mechanics.Rogue.GeneratesCharge(cast.Skill.Code)) return;
        await AddChargeAsync(executor.Statuses, cast.Battle.Room, cast.Source.Id, cast.Battle.Logs, cast.Source.Label, cast.Skill.Code, _mechanics);
    }

    public static async Task<bool> AddChargeAsync(BattleStatusService statuses, Room room, int characterId,
        List<string> logs, string label, string? skillCode = null, ProfessionMechanicCatalog? mechanics = null)
    {
        var definition = statuses.Catalog.FindMechanic(BattleStatusMechanic.ShadowCharges);
        if (definition is null) return false;
        var current = await statuses.StacksAsync(room, "Character", characterId, definition.Code);
        var charge = await statuses.SetCounterAsync(room, "Character", characterId, definition.Code,
            Math.Min(definition.MaxStacks, current + (mechanics ?? ProfessionMechanicCatalog.Default).Rogue.ChargeGain), new("Character", characterId, skillCode));
        logs.Add($"{label} 获得 {definition.Name}（{charge.Stacks} 层）。");
        return true;
    }
}
