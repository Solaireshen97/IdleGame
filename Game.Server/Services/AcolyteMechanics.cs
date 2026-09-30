using Game.Shared.Enums;
using Game.Shared.Models;

namespace Game.Server.Services;

public sealed class AcolyteMechanics(ProfessionMechanicCatalog? mechanics = null) : ProfessionCastMechanic
{
    private readonly ProfessionMechanicCatalog _mechanics = mechanics ?? ProfessionMechanicCatalog.Default;
    private bool _damageEnhanced, _healEnhanced, _cleanseRevelation;
    public static bool NeedsSelfCleanse(CharacterSkillDefinition skill, ProfessionMechanicCatalog? mechanics = null) => !skill.IsShared &&
        skill.ProfessionCode == "acolyte" && skill.Code == "acolyte-purify" &&
        skill.Level >= (mechanics ?? ProfessionMechanicCatalog.Default).Acolyte.PurifySelfCleanseMinLevel;

    public override async Task PrepareAsync(BattleCastExecution cast, BattleEffectExecutor executor)
    {
        if (executor.Statuses is not { } statuses) return;
        _damageEnhanced = await HasEnhancementAsync(statuses, cast.Battle.Room, cast.Source.Id, false, cast.Skill.Code != "acolyte-revelation");
        _healEnhanced = await HasEnhancementAsync(statuses, cast.Battle.Room, cast.Source.Id, true);
        _cleanseRevelation = cast.Skill.Code == "acolyte-purify" &&
            (await statuses.MechanicStatesAsync(cast.Battle.Room, "Character", cast.Source.Id, BattleStatusMechanic.Revelation)).Any(effect => effect.Stacks > 0);
        if (_damageEnhanced) cast.DamageMultipliers.Add(1 + _mechanics.Acolyte.DamageEnhancementPercent / 100m);
        if (_healEnhanced) cast.HealingMultiplier = 1 + _mechanics.Acolyte.HealingEnhancementPercent / 100m;
    }

    public override IReadOnlyList<BattleActor> OrderTargets(BattleCastExecution cast, BattleSkillEffect effect, IReadOnlyList<BattleActor> targets) =>
        effect.Kind == BattleEffectKind.Cleanse && cast.CharacterSkill!.Level >= _mechanics.Acolyte.PurifySelfCleanseMinLevel && cast.ChosenTargetId is null
            ? targets.OrderBy(target => target.Id == cast.Source.Id).ToList() : targets;

    public override async Task AfterEffectAsync(BattleCastExecution cast, BattleSkillEffect effect, IReadOnlyList<BattleEffectOutcome> outcomes,
        BattleEffectExecutor executor)
    {
        if (effect.Kind != BattleEffectKind.Cleanse || cast.Skill.Code != "acolyte-purify" || executor.Statuses is null) return;
        var primaryTargetId = cast.ChosenTargetId ?? outcomes.FirstOrDefault(outcome => outcome.RemovedStatus is not null)?.Target.Id;
        if (cast.CharacterSkill!.Level >= _mechanics.Acolyte.PurifySelfCleanseMinLevel && primaryTargetId != cast.Source.Id)
            await executor.CleanseAsync(cast, cast.Source);
        if (_cleanseRevelation && primaryTargetId is int id)
        {
            var target = cast.Battle.Characters.SingleOrDefault(actor => actor.Id == id);
            if (target is not null) await executor.CleanseAsync(cast, target);
        }
    }

    public override async Task AfterCastAsync(BattleCastExecution cast, BattleEffectExecutor executor)
    {
        if (!cast.Result.Applied || executor.Statuses is not { } statuses) return;
        var room = cast.Battle.Room;
        if (cast.Result.Damage > 0)
        {
            if (_damageEnhanced)
            {
                await ConsumeEnhancementAsync(statuses, room, cast.Source.Id, false, cast.Skill.Code != "acolyte-revelation", _mechanics);
                cast.Battle.Logs.Add($"{cast.Source.Label} 的辉光使 {cast.Skill.Name} 伤害提高 {_mechanics.Acolyte.DamageEnhancementPercent}%。");
            }
            await GrantEnhancementAsync(statuses, room, cast.Source.Id, true, cast.Skill.Code, _mechanics);
        }
        if (cast.Skill.Code == "acolyte-revelation")
        {
            var charges = _mechanics.Acolyte.RevelationChargesByLevel.ForLevel(cast.CharacterSkill!.Level);
            await SetRevelationAsync(statuses, room, cast.Source.Id, charges, cast.Skill.Code);
            cast.Battle.Logs.Add($"{cast.Source.Label} 的神启使接下来 {charges} 次本职技能固定获得强化。");
        }
        if (cast.Result.HealingOccurred)
        {
            if (_healEnhanced)
            {
                await ConsumeEnhancementAsync(statuses, room, cast.Source.Id, true, mechanics: _mechanics);
                cast.Battle.Logs.Add($"{cast.Source.Label} 的恩泽使 {cast.Skill.Name} 治疗提高 {_mechanics.Acolyte.HealingEnhancementPercent}%。");
            }
            await GrantEnhancementAsync(statuses, room, cast.Source.Id, false, cast.Skill.Code, _mechanics);
        }
        if (cast.Result.CleansingOccurred)
        {
            if (_cleanseRevelation) await ConsumeRevelationAsync(statuses, room, cast.Source.Id, _mechanics);
            await GrantEnhancementAsync(statuses, room, cast.Source.Id, false, cast.Skill.Code, _mechanics);
        }
    }

    public static async Task<bool> HasEnhancementAsync(BattleStatusService statuses, Room room, int id, bool healing, bool includeRevelation = true)
    {
        var kind = healing ? BattleStatusMechanic.NextNativeHeal : BattleStatusMechanic.NextNativeDamage;
        return (await statuses.MechanicStatesAsync(room, "Character", id, kind)).Count > 0 || includeRevelation &&
            (await statuses.MechanicStatesAsync(room, "Character", id, BattleStatusMechanic.Revelation)).Any(effect => effect.Stacks > 0);
    }

    public static async Task ConsumeEnhancementAsync(BattleStatusService statuses, Room room, int id, bool healing, bool includeRevelation = true,
        ProfessionMechanicCatalog? mechanics = null)
    {
        var definition = statuses.Catalog.FindMechanic(healing ? BattleStatusMechanic.NextNativeHeal : BattleStatusMechanic.NextNativeDamage);
        if (definition is not null) await statuses.ConsumeAsync(room, "Character", id, definition.Code);
        if (includeRevelation) await ConsumeRevelationAsync(statuses, room, id, mechanics);
    }

    public static async Task ConsumeRevelationAsync(BattleStatusService statuses, Room room, int id, ProfessionMechanicCatalog? mechanics = null)
    {
        var definition = statuses.Catalog.FindMechanic(BattleStatusMechanic.Revelation);
        if (definition is not null) await statuses.ConsumeAsync(room, "Character", id, definition.Code,
            (mechanics ?? ProfessionMechanicCatalog.Default).Acolyte.RevelationConsumption);
    }

    public static async Task GrantEnhancementAsync(BattleStatusService statuses, Room room, int id, bool healing, string? skillCode = null,
        ProfessionMechanicCatalog? mechanics = null)
    {
        var definition = statuses.Catalog.FindMechanic(healing ? BattleStatusMechanic.NextNativeHeal : BattleStatusMechanic.NextNativeDamage);
        if (definition is not null) await statuses.SetCounterAsync(room, "Character", id, definition.Code,
            (mechanics ?? ProfessionMechanicCatalog.Default).Acolyte.EnhancementCharges, new("Character", id, skillCode));
    }

    public static async Task SetRevelationAsync(BattleStatusService statuses, Room room, int id, int charges, string? skillCode = null)
    {
        var definition = statuses.Catalog.FindMechanic(BattleStatusMechanic.Revelation);
        if (definition is not null) await statuses.SetCounterAsync(room, "Character", id, definition.Code, charges, new("Character", id, skillCode));
    }
}
