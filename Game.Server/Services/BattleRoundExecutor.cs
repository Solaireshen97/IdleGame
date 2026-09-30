using Game.Server.Configuration;
using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public enum BattleRoundOutcome { Continue, MonsterDefeated, PartyDefeated }

/// <summary>
/// Runs combat actions against tracked state without saving, awarding rewards, changing waves,
/// or scheduling rooms. The caller owns the settlement/event scopes and commits the whole round.
/// </summary>
public sealed partial class BattleRoundExecutor(GameDbContext dbContext, ConsumableCatalog consumableCatalog,
    SkillCatalog skillCatalog, BattleStatusService statuses, BattleEffectExecutor effects,
    MonsterCombatService? monsterCombatService = null, WeaponCatalog? weaponCatalog = null,
    SoulImprintCatalog? soulImprintCatalog = null, Random? random = null, ProfessionMechanicCatalog? mechanics = null)
{
    private readonly SkillBattleSnapshotFactory _skillSnapshots = new(dbContext, skillCatalog, monsterCombatService, mechanics);
    private readonly LegacyBattleTalentService LegacyTalents = new(dbContext, skillCatalog, statuses);
    private readonly ProfessionMechanicRegistry _professionMechanics = new(mechanics);
    private BattleStatusService Statuses => statuses;
    private BattleEffectExecutor Effects => effects;
    private BattleEventCollector _events => effects.Events;

    public async Task<BattleRoundOutcome> ExecuteAsync(Room room, Monster monster, List<BattleParticipant> slots,
        IReadOnlyCollection<int> autoCharacterIds, List<string> logs)
    {
        var aliveSlots = slots.Where(entry => entry.Character.Hp > 0).OrderBy(entry => entry.Slot.SlotIndex).ToList();
        var combatParticipants = slots.OrderBy(entry => entry.Slot.SlotIndex).ToList();
        var characterIds = aliveSlots.Select(entry => entry.Character.Id).ToList();
        var operationBonuses = await ApplyOperationPotionsAsync(room, aliveSlots, logs);
        await UpdateTemporaryWeaponBonusesAsync(room, aliveSlots);
        await ApplyCombatBuffsAsync(room, aliveSlots, logs);
        await UpdateTemporaryWeaponBonusesAsync(room, aliveSlots);
        var mainWeaponElements = await dbContext.CharacterWeapons
            .Where(weapon => characterIds.Contains(weapon.CharacterId) && weapon.EquippedSlotIndex == WeaponRules.MainSlotIndex)
            .ToDictionaryAsync(weapon => weapon.CharacterId, weapon => weapon.Element);
        var pendingTalentEcho = new Dictionary<int, decimal>();
        foreach (var entry in aliveSlots)
        {
            pendingTalentEcho[entry.Character.Id] = await LegacyTalents.ConsumeEchoAsync(room, entry.Character.Id);
        }
        await ApplySoulImprintsAsync(room, aliveSlots, monster, operationBonuses, autoCharacterIds, logs);
        await ApplyCombatSkillsAsync(room, aliveSlots, monster, mainWeaponElements,
            operationBonuses, autoCharacterIds, logs);
        var battle = new BattleExecutionContext(room, monster,
            slots.Select(entry => new BattleParticipant(entry.Slot, entry.Character)).ToList(), mainWeaponElements, operationBonuses, logs);
        foreach (var entry in aliveSlots)
        {
            if (monster.Hp <= 0) break;
            var stats = battle.StatsFor(entry.Character);
            var talentEcho = pendingTalentEcho[entry.Character.Id];
            var healthPercent = WeaponCombatRules.HealthDamagePercent(entry.Character.Hp, stats.MaxHp,
                stats.StaminaPercent, stats.EnmityPercent);
            var adrenalineChance = monsterCombatService is null ? 0m :
                await monsterCombatService.GetModifierAsync(room, "Character", entry.Character.Id, "DoubleAttackChancePercent");
            var coordinatedEcho = monsterCombatService is null ? 0 :
                await monsterCombatService.Statuses.MechanicPowerAsync(room, "Character", entry.Character.Id, BattleStatusMechanic.HunterCoordinated);
            var hits = WeaponCombatRules.RollPercent(stats.DoubleAttackChancePercent + adrenalineChance, random) ? 2 : 1;
            for (var hit = 0; hit < hits && monster.Hp > 0; hit++)
            {
                var source = BattleActor.ForCharacter(new(entry.Slot, entry.Character), stats);
                using var attackAction = _events.ActionScope(source, "normal-attack", hit == 0 ? "普通攻击" : "二连击", BattleActionKind.NormalAttack);
                var damageResult = await Effects.Damage.CharacterDamageAsync(battle, source,
                    BattleSkillEffect.Damage(100), BattleDamageOrigin.NormalAttack, true, healthPercent);
                var damage = damageResult.CalculatedAmount;
                var critical = damageResult.IsCritical;
                logs.Add($"{entry.Slot.SlotIndex}号位 {entry.Character.Name} {(hit == 0 ? "普通攻击" : "二连击")} {monster.Name}，造成 {damage} 点伤害{(critical ? "（暴击）" : "")}。");
                var echo = WeaponCombatRules.EchoDamage(damage,
                    stats.NormalEchoPercent + talentEcho + coordinatedEcho);
                if (monster.Hp > 0 && echo > 0)
                {
                    var before = monster.Hp;
                    monster.Hp = Math.Max(0, monster.Hp - echo);
                    using var followUp = _events.ActionScope(source, "normal-echo", "普攻追击", BattleActionKind.FollowUp);
                    _events.Hp(room, BattleEventKind.Damage, source, battle.Enemy, echo, before - monster.Hp, before,
                        element: mainWeaponElements.TryGetValue(source.Id, out var echoElement) ? echoElement : null, modifier: WeaponCombatRules.ElementAttackPercent(
                            mainWeaponElements.TryGetValue(source.Id, out var echoModifierElement) ? echoModifierElement : null, monster.Element, stats.ElementAdvantagePercent));
                    logs.Add($"{entry.Slot.SlotIndex}号位 {entry.Character.Name} 对 {monster.Name} 造成 {echo} 点普攻追击伤害。");
                }
            }
            if (monster.Hp <= 0) break;
        }
        if (monster.Hp <= 0)
        {
            // A player kill completes healing auras, but skips enemy damage and hostile periodic effects.
            if (monsterCombatService is not null)
                await monsterCombatService.ResolveEndOfRoundAsync(room, monster, combatParticipants, logs,
                    operationBonuses, healingOnly: true);
            await LegacyTalents.VictoryAsync(battle, characterIds);
            return BattleRoundOutcome.MonsterDefeated;
        }

        await ApplyCombatConsumablesAsync(room, aliveSlots, logs);
        if (!slots.Any(entry => entry.Character.Hp > 0)) return BattleRoundOutcome.PartyDefeated;
        if (monsterCombatService is not null)
        {
            await monsterCombatService.ExecuteIntentAsync(room, monster, combatParticipants,
                mainWeaponElements, logs, operationBonuses);
            await monsterCombatService.ResolveEndOfRoundAsync(room, monster, combatParticipants, logs,
                operationBonuses);
        }
        else
        {
            var target = slots.Where(entry => entry.Character.Hp > 0).OrderBy(entry => entry.Slot.SlotIndex).First();
            using var attackAction = _events.ActionScope(battle.Enemy, "basic-attack", "普通攻击", BattleActionKind.NormalAttack);
            var damageResult = await Effects.Damage.MonsterDamageAsync(battle, BattleActor.ForCharacter(target, battle.StatsFor(target.Character)),
                new(BattleEffectKind.Damage, BattleEffectTarget.ForMonster("Front")), false);
            logs.Add($"{monster.Name} 普通攻击 {target.Slot.SlotIndex}号位 {target.Character.Name}，造成 {damageResult.CalculatedAmount} 点伤害。");
            await Statuses.ResolveEndOfRoundAsync(room, monster, combatParticipants, logs, operationBonuses);
        }
        // A counter or periodic effect can defeat the monster during the enemy phase.
        if (monster.Hp <= 0)
        {
            await LegacyTalents.VictoryAsync(battle, characterIds);
            return BattleRoundOutcome.MonsterDefeated;
        }
        return slots.Any(entry => entry.Character.Hp > 0) ? BattleRoundOutcome.Continue : BattleRoundOutcome.PartyDefeated;
    }

    /// <summary>Runs after the caller decides the wave/outcome and before advancing the round number.</summary>
    public async Task FinishRoundEffectsAsync(Room room, IReadOnlyList<BattleParticipant> party)
    {
        await CaptureConsumableExpiryAsync(room);
        await effects.Guards.ClearRoundAsync(room, party.Select(entry => entry.Character.Id));
    }
}
