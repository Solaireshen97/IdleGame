using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Dtos;
using Game.Server.Configuration;
using Game.Shared.Models;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public enum BattleDamageOrigin { NormalAttack, Skill, Counter, Mechanic, LegacyParry }

/// <summary>One numerical pipeline, with explicit origin flags for the existing damage rules.</summary>
public sealed partial class BattleDamageService(BattleStatusService? statuses, BattleGuardService guards, Random? random = null,
    BattleEventCollector? events = null, DungeonRunRulesService? runRules = null, IOptions<CombatDamageOptions>? damageOptions = null,
    MonsterPhaseService? phases = null)
{
    private readonly decimal _variancePercent = DamageVariance.ValidatePercent(damageOptions?.Value.VariancePercent ?? 0);
    public BattleEventCollector Events { get; } = events ?? statuses?.Events ?? new();

    private async Task<int> VaryAsync(Room room, int damage)
    {
        if (damage <= 0) return damage;
        var percent = runRules is null ? _variancePercent : (await runRules.EnsureAsync(room)).DirectDamageVariancePercent;
        return DamageVariance.Roll(damage, percent, random ?? Random.Shared);
    }
    public async Task<BattleDamageResult> CharacterDamageAsync(BattleExecutionContext battle, BattleActor source,
        BattleSkillEffect effect, BattleDamageOrigin origin, bool rollCritical, decimal? healthSnapshot = null,
        decimal conditionalBonus = 0, decimal attackPowerBonus = 0, IReadOnlyList<decimal>? multipliers = null, ElementType? damageElement = null)
    {
        var character = source.Character ?? throw new InvalidOperationException("Character damage needs a character source.");
        if (character.Hp <= 0) return new(0, 0, false);
        if (statuses is not null && await statuses.IsActionBlockedAsync(battle.Room, source.Id)) return new(0, 0, false);
        var stats = battle.StatsFor(character);
        var monster = battle.Monster;
        if (monster.Hp <= 0) return new(0, 0, false);
        var potion = battle.OperationBonuses.GetValueOrDefault(source.Id);
        var element = damageElement ?? (battle.MainWeaponElements.TryGetValue(source.Id, out var main) ? main : (ElementType?)null);
        var isSkill = origin != BattleDamageOrigin.NormalAttack;
        var isLegacyParry = origin == BattleDamageOrigin.LegacyParry;
        var critical = rollCritical && WeaponCombatRules.RollPercent(stats.CriticalChancePercent +
            (isSkill ? stats.SkillCriticalChancePercent : 0), random);
        var attack = statuses is null || isLegacyParry ? 0 : await statuses.ModifierAsync(battle.Room, "Character", source.Id, "AttackPercent");
        var reduction = statuses is null || isLegacyParry ? 0 : await statuses.ModifierAsync(battle.Room, "Monster", monster.Id, "ReductionPercent");
        if (!isLegacyParry) reduction += (await guards.DefenseAsync(battle.Room, "Monster", monster.Id)).ReductionPercent;
        var health = healthSnapshot ?? WeaponCombatRules.HealthDamagePercent(character.Hp, stats.MaxHp,
            stats.StaminaPercent, stats.EnmityPercent);
        var damage = DamageCalculator.Calculate(stats.Attack, monster.Defense, effect.Power,
            new DamageFactors(AttackPercent: WeaponCombatRules.AttackBonusPercent(stats, battle.Room.RoundNumber) + attack + potion.AttackPercent +
                    (isSkill ? 0 : stats.NormalAttackPercent),
                HealthPercent: isLegacyParry ? 0 : health, CriticalPercent: critical ? BattleRules.CriticalDamageBonusPercent : 0,
                ElementPercent: isLegacyParry ? 0 : WeaponCombatRules.ElementAttackPercent(element, monster.Element, stats.ElementAdvantagePercent),
                ReductionPercent: reduction, SkillDamagePercent: isSkill && !isLegacyParry ? stats.SkillDamagePercent + conditionalBonus : 0,
                ConsumablePercent: potion.FinalDamagePercent + (isSkill ? 0 : potion.NormalAttackDamagePercent)),
            effect.AttackPowerPercent + attackPowerBonus);
        foreach (var multiplier in multipliers ?? [])
            damage = (int)Math.Min(int.MaxValue, decimal.Floor(damage * multiplier));
        if (statuses is not null) damage = await statuses.AmplifyDamageAsync(battle.Room, monster.Id, damage);
        if (statuses is not null && !isLegacyParry)
        {
            var dealt = await statuses.ModifierAsync(battle.Room, "Character", source.Id, "DamageDealtPercent");
            if (dealt != 0) damage = (int)Math.Min(int.MaxValue, decimal.Floor(damage * Math.Max(0m, 1m + dealt / 100m)));
        }
        damage = await VaryAsync(battle.Room, damage);
        var before = monster.Hp;
        var actual = Math.Min(before, damage);
        monster.Hp = Math.Max(0, monster.Hp - damage);
        Events.Hp(battle.Room, BattleEventKind.Damage, source, battle.Enemy, damage, actual, before, critical,
            isLegacyParry ? null : element, isLegacyParry ? 0 : WeaponCombatRules.ElementAttackPercent(element, monster.Element, stats.ElementAdvantagePercent));
        await ObserveDirectDamageAsync(battle, isLegacyParry ? null : element, actual, isLegacyParry ? null : source.Id);
        if (!isLegacyParry) await ReflectCharacterDamageAsync(battle, source, actual);
        return new(damage, actual, critical);
    }

    public Task ObserveDirectDamageAsync(BattleExecutionContext battle, ElementType? element, int actualDamage,
        int? sourceCharacterId = null) =>
        phases?.ObserveDirectDamageAsync(battle, element, actualDamage, sourceCharacterId) ?? Task.CompletedTask;

    public async Task ObserveCleanseAsync(BattleExecutionContext battle, int characterId, string statusCode)
    {
        if (phases is null) return;
        await phases.ObserveDeepColdCleanseAsync(battle, characterId, statusCode);
        await phases.ObservePlagueCleanseAsync(battle, characterId, statusCode);
    }

    public Task ObserveDispelAsync(BattleExecutionContext battle, string statusCode) =>
        phases?.ObserveReflectionMirrorDispelAsync(battle, statusCode) ?? Task.CompletedTask;

    public async Task<bool> SuppressMonsterStatusAsync(BattleExecutionContext battle, int characterId, string statusCode) =>
        phases is not null && (await phases.SuppressBasicColdAsync(battle.Room, battle.Monster, characterId, statusCode) ||
            await phases.SuppressBasicPoisonAsync(battle.Room, battle.Monster, characterId, statusCode));

    public async Task<BattleDamageResult> MonsterDamageAsync(BattleExecutionContext battle, BattleActor target,
        BattleSkillEffect effect, bool areaAttack, decimal skillReduction = 0, int legacyReduction = 0,
        decimal skillBonusPercent = 0)
    {
        var character = target.Character ?? throw new InvalidOperationException("Monster direct damage needs a character target.");
        var stats = battle.StatsFor(character);
        var monster = battle.Monster;
        var potion = battle.OperationBonuses.GetValueOrDefault(target.Id);
        var element = battle.MainWeaponElements.TryGetValue(target.Id, out var main) ? main : (ElementType?)null;
        var attack = statuses is null ? 0 : await statuses.ModifierAsync(battle.Room, "Monster", monster.Id, "AttackPercent");
        var reduction = statuses is null ? 0 : await statuses.ModifierAsync(battle.Room, "Character", target.Id, "ReductionPercent");
        var guard = await guards.DefenseAsync(battle.Room, target.Id);
        var skillMultiplier = (1m - skillReduction / 100m) * (1m + skillBonusPercent / 100m);
        var scaledAttack = effect.AttackPowerPercent > 0 ? Math.Max(1, (int)decimal.Floor(monster.Attack * effect.AttackPowerPercent / 100m * skillMultiplier)) : 0;
        var scaledFlat = (int)decimal.Floor(effect.Power * skillMultiplier);
        var damage = DamageCalculator.Calculate(scaledAttack, 0, scaledFlat, factors: new DamageFactors(AttackPercent: attack,
            ElementPercent: ElementMatchup.MonsterAttackPercent(monster.Element, element),
            ReductionPercent: WeaponCombatRules.CombinedDirectReductionPercent(guard.ReductionPercent + reduction + legacyReduction +
                (areaAttack ? potion.AreaDamageReductionPercent : 0) - potion.DamageTakenPercent, stats, character.Hp)));
        damage = await VaryAsync(battle.Room, damage);
        var before = character.Hp;
        var actual = Math.Min(before, damage);
        character.Hp = Math.Max(0, character.Hp - damage);
        Events.Hp(battle.Room, BattleEventKind.Damage, battle.Enemy, target, damage, actual, before,
            element: monster.Element, modifier: ElementMatchup.MonsterAttackPercent(monster.Element, element));
        if (guard.KnightCounterEligible) await guards.RecordHitAsync(battle.Room, target.Id, monster.Id);
        return new(damage, actual, false);
    }

    public static int Heal(BattleActor source, BattleActor target, BattleSkillEffect effect, decimal multiplier = 1, bool applyHealingBonuses = true)
    {
        if (target.Hp <= 0 || target.Hp >= target.MaxHp) return 0;
        return RestoreHp(target, CalculateHealing(source, target, effect, multiplier, applyHealingBonuses));
    }

    public static int CalculateHealing(BattleActor source, BattleActor target, BattleSkillEffect effect, decimal multiplier = 1, bool applyHealingBonuses = true)
    {
        var bonus = applyHealingBonuses ? (source.Stats?.HealingDonePercent ?? 0) + (target.Stats?.HealingReceivedPercent ?? 0) : 0;
        var raw = (int)decimal.Floor(RecoveryCalculator.Calculate(target.MaxHp, effect.Power, effect.HealMaxHpPercent) * (1 + bonus / 100m));
        return (int)Math.Min(int.MaxValue, decimal.Floor(raw * multiplier));
    }

    public static int RestoreHp(BattleActor target, int amount)
    {
        if (target.Hp <= 0) return 0;
        var restored = Math.Min(Math.Max(0, amount), Math.Max(0, target.MaxHp - target.Hp));
        target.Hp += restored;
        return restored;
    }
}
