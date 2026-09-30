using Game.Shared.Enums;
using Game.Shared.Models;

namespace Game.Server.Services;

public sealed class HunterMechanics(ProfessionMechanicCatalog? mechanics = null) : ProfessionCastMechanic
{
    private readonly ProfessionMechanicCatalog _mechanics = mechanics ?? ProfessionMechanicCatalog.Default;
    private bool _marked;
    public override async Task PrepareAsync(BattleCastExecution cast, BattleEffectExecutor executor)
    {
        if (executor.Statuses is not { } statuses || cast.Skill.Code is not ("hunter-precision-shot" or "hunter-expose-shot" or "hunter-hunting-signal")) return;
        _marked = await HasMarkAsync(statuses, cast.Battle.Room, cast.Source.Id, cast.Battle.Monster.Id);
        if (_marked && cast.Skill.Code == "hunter-precision-shot")
            cast.AttackPowerBonus = _mechanics.Hunter.MarkedPrecisionAttackBonusPercent.ForLevel(cast.CharacterSkill!.Level);
    }

    public override BattleSkillEffect TransformEffect(BattleCastExecution cast, BattleSkillEffect effect, BattleEffectExecutor executor)
    {
        if (!_marked || effect.Kind != BattleEffectKind.ApplyStatus ||
            executor.Statuses?.CatalogFor(cast.Battle.Room).Find(effect.StatusCode)?.Mechanic != BattleStatusMechanic.HunterCoordinated) return effect;
        var definition = _mechanics.TryHunterCoordinatedStatus(executor.Statuses.CatalogFor(cast.Battle.Room), cast.CharacterSkill!.Level);
        return definition is null ? effect : effect with { StatusCode = definition.Code };
    }

    public override async Task AfterCastAsync(BattleCastExecution cast, BattleEffectExecutor executor)
    {
        if (!_marked || executor.Statuses is not { } statuses) return;
        if (cast.Skill.Code == "hunter-expose-shot" && cast.Battle.Monster.Hp > 0)
        {
            var definition = _mechanics.TryHunterVulnerabilityStatus(statuses.CatalogFor(cast.Battle.Room), cast.CharacterSkill!.Level);
            if (definition is not null) cast.Result.Outcomes.Add(await executor.ApplyStatusAsync(cast,
                new(BattleEffectKind.ApplyStatus, BattleEffectTarget.ForCharacter("Monster"), AttackPowerPercent: 0, StatusCode: definition.Code, DurationRounds: _mechanics.Hunter.VulnerabilityDurationRounds),
                cast.Battle.Enemy));
        }
        if (cast.Result.Applied) await ConsumeMarkAsync(statuses, cast.Battle.Room, cast.Source.Id, cast.Battle.Monster.Id, cast.Battle.Logs, cast.Source.Label, _mechanics);
    }

    public static async Task<bool> HasMarkAsync(BattleStatusService statuses, Room room, int id, int monsterId) =>
        (await statuses.MechanicStatesAsync(room, "Character", id, BattleStatusMechanic.HunterMark))
            .Any(effect => effect.BoundTargetType == "Monster" && effect.BoundTargetId == monsterId);

    public static async Task<bool> ConsumeMarkAsync(BattleStatusService statuses, Room room, int id, int monsterId, List<string> logs, string label,
        ProfessionMechanicCatalog? mechanics = null)
    {
        var mark = (await statuses.MechanicStatesAsync(room, "Character", id, BattleStatusMechanic.HunterMark))
            .FirstOrDefault(effect => effect.BoundTargetType == "Monster" && effect.BoundTargetId == monsterId);
        if (mark is null) return false;
        var eagle = (await statuses.MechanicStatesAsync(room, "Character", id, BattleStatusMechanic.HunterEagleEye)).FirstOrDefault(effect => effect.Stacks > 0);
        if (eagle is not null)
        {
            await statuses.ConsumeAsync(room, "Character", id, eagle.EffectCode,
                (mechanics ?? ProfessionMechanicCatalog.Default).Hunter.EagleEyeConsumption);
            logs.Add($"{label} 借助鹰眼时刻保留了猎物标记。");
            return true;
        }
        await statuses.ConsumeAsync(room, "Character", id, mark.EffectCode);
        logs.Add($"{label} 消耗了猎物标记。");
        return true;
    }
}
