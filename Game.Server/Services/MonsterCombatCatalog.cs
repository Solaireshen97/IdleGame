using Game.Server.Configuration;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class MonsterCombatCatalog
{
    public BattleStatusCatalog Statuses { get; }
    private readonly Dictionary<string, MonsterSkillOptions> _skills = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MonsterCombatProfileOptions> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MonsterSkillDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MonsterCombatProfile> _compiledProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int[]> _depthStages = new(StringComparer.OrdinalIgnoreCase);
    private readonly MonsterCombatOptions _source;

    public MonsterCombatCatalog(IOptions<MonsterCombatOptions> options, BattleStatusCatalog? statuses = null)
    {
        _source = Copy(options.Value);
        var content = Copy(_source);
        Statuses = statuses ?? new BattleStatusCatalog(Options.Create(_source));

        foreach (var skill in content.Skills)
        {
            if (string.IsNullOrWhiteSpace(skill.Code) || string.IsNullOrWhiteSpace(skill.Name) ||
                string.IsNullOrWhiteSpace(skill.Description) || skill.TargetType is not ("Self" or "Front" or "AllAlive" or "RandomAlive") ||
                skill.DamagePowerPercent < 0 || skill.CooldownRounds < 0 || skill.InitialCooldownRounds < 0 ||
                skill.SelfHpBelowPercent is < 1 or > 100 ||
                skill.RoomRoundAtLeast is < 1 or > 250 || skill.ForcedPriority is < 0 or > 100 ||
                skill.DangerLevel is not ("Normal" or "Dangerous" or "Deadly") ||
                (skill.Effects is null ? skill.DamagePowerPercent == 0 && skill.Statuses.Count == 0 :
                    skill.DamagePowerPercent != 0 || skill.Statuses.Count != 0 || skill.Effects.Count == 0 ||
                    skill.Effects.Any(effect => !ValidEffect(effect, skill.TargetType))) ||
                skill.Statuses.Any(status => status.DurationRounds <= 0 || Statuses.Find(status.StatusCode) is null) ||
                !_skills.TryAdd(skill.Code, skill))
                throw new InvalidOperationException($"Invalid monster skill configuration: {skill.Code}");
        }

        foreach (var (code, profile) in content.Profiles)
        {
            if (string.IsNullOrWhiteSpace(code) || profile.SkillUseChancePercent is < 0 or > 100 ||
                profile.Skills.Any(skill => skill.Weight <= 0 || !_skills.ContainsKey(skill.Code)) ||
                profile.Skills.Select(skill => skill.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profile.Skills.Count ||
                !ValidFireCore(profile) || !ValidDeepCold(profile) || !ValidEarthArmor(profile) || !ValidStaticField(profile) ||
                !ValidReflectionMirror(profile) || !ValidPlaguePoison(profile) ||
                !_profiles.TryAdd(code, profile))
                throw new InvalidOperationException($"Invalid monster combat profile: {code}");
        }

        foreach (var (code, stages) in _source.DepthProgressions)
        {
            if (string.IsNullOrWhiteSpace(code) || stages.Count == 0 ||
                stages.Select(stage => stage.Depth).Distinct().Count() != stages.Count ||
                stages.Any(stage => stage.Depth is < 2 or > 100 ||
                    stage.ReplacementProfileCode is not null && !_profiles.ContainsKey(stage.ReplacementProfileCode) ||
                    stage.AddedSkills.Any(skill => skill.Weight <= 0 || !_skills.ContainsKey(skill.Code))))
                throw new InvalidOperationException($"Invalid monster depth progression: {code}");
        }
        foreach (var (baseCode, baseProfile) in _profiles.ToArray())
        {
            if (baseProfile.DepthProgressionCode is null) continue;
            if (!_source.DepthProgressions.TryGetValue(baseProfile.DepthProgressionCode, out var progression))
                throw new InvalidOperationException($"Unknown monster depth progression for {baseCode}: {baseProfile.DepthProgressionCode}");
            var stages = progression.OrderBy(stage => stage.Depth).ToArray();
            _depthStages.Add(baseCode, stages.Select(stage => stage.Depth).ToArray());
            var current = CopyProfile(baseProfile);
            foreach (var stage in stages)
            {
                if (stage.ReplacementProfileCode is not null)
                    current = CopyProfile(_source.Profiles[stage.ReplacementProfileCode]);
                current.Skills.AddRange(stage.AddedSkills.Select(skill => new MonsterProfileSkillOptions
                    { Code = skill.Code, Weight = skill.Weight }));
                if (current.Skills.Select(skill => skill.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != current.Skills.Count)
                    throw new InvalidOperationException($"Duplicate skill in monster depth progression: {baseCode}, LV{stage.Depth}");
                var code = DepthProfileCode(baseCode, stage.Depth);
                if (!_profiles.TryAdd(code, CopyProfile(current)))
                    throw new InvalidOperationException($"Reserved depth profile code: {code}");
            }
        }
        foreach (var skill in _skills.Values)
            _definitions.Add(skill.Code, MonsterSkillDefinition.Compile(skill));
        foreach (var (code, profile) in _profiles)
            _compiledProfiles.Add(code, new(profile.SkillUseChancePercent,
                profile.Skills.Select(skill => new MonsterProfileSkill(skill.Code, skill.Weight)).ToImmutableArray(),
                profile.UseEncounterLocalSkillClock));
    }

    private bool ValidEffect(CombatSkillEffectOptions effect, string intentTarget)
    {
        if (effect.Target is not ("Self" or "Front" or "AllAlive" or "RandomAlive") ||
            effect.Target != "Self" && effect.Target != intentTarget || effect.Power < 0 ||
            effect.AttackPowerPercent is < 0 or > 1000 || effect.HealMaxHpPercent is < 0 or > 100) return false;
        return effect.Type switch
        {
            "Damage" => effect.Target != "Self" && (effect.Power > 0 || effect.AttackPowerPercent > 0),
            "Heal" => effect.Target == "Self" && (effect.Power > 0 || effect.HealMaxHpPercent > 0),
            "Guard" => effect.Target == "Self" && effect.Power is > 0 and <= 100,
            "Cleanse" => effect.Target == "Self",
            "Dispel" => effect.Target != "Self",
            "ApplyStatus" => effect.DurationRounds > 0 && Statuses.Find(effect.StatusCode) is not null,
            _ => false
        };
    }

    private bool ValidFireCore(MonsterCombatProfileOptions profile)
    {
        if (profile.FireCore is not { } core) return true;
        var validTrigger = core.TriggerHpPercent is { } hp
            ? hp is >= 1 and <= 99 && core.FirstActivationRound == 0 && core.CycleRounds == 0
            : core.FirstActivationRound is >= 1 and <= 250 && core.CycleRounds is >= 1 and <= 250 &&
                core.CycleRounds > core.WindowRounds + core.RewardRounds;
        var validLink = core.ExtraTargetCount == 0
            ? core.ExtraAttackPowerPercent == 0 && string.IsNullOrEmpty(core.LinkedSkillCode)
            : core.ExtraTargetCount is >= 1 and <= 4 && core.ExtraAttackPowerPercent is > 0 and <= 1000 &&
                profile.Skills.Any(skill => skill.Code == core.LinkedSkillCode) &&
                _skills.TryGetValue(core.LinkedSkillCode, out var linked) && linked.TargetType == "Front";
        return validTrigger && validLink &&
            core.WindowRounds is >= 1 and <= 10 && core.RewardRounds is >= 1 and <= 10 &&
            core.BreakWaterDamagePercent is > 0 and <= 100 &&
            Statuses.Find(core.HeatingStatusCode) is { EffectType: "AttackPercent", IsDispellable: false, IsPositive: true,
                Lifetime: Game.Shared.Enums.BattleStatusLifetime.Rounds, ValuePerStack: > 0 } &&
            Statuses.Find(core.RewardStatusCode) is { EffectType: "DamageTakenPercent", IsDispellable: false, IsPositive: false,
                Lifetime: Game.Shared.Enums.BattleStatusLifetime.Rounds, ValuePerStack: > 0 };
    }

    private bool ValidPlaguePoison(MonsterCombatProfileOptions profile)
    {
        if (profile.PlaguePoison is not { } poison) return true;
        var codes = new[] { poison.PoisonStatusCode, poison.TargetStatusCode, poison.TickUsedStatusCode, poison.RewardStatusCode };
        return profile.FireCore is null && profile.DeepCold is null && profile.EarthArmor is null &&
            profile.StaticField is null && profile.ReflectionMirror is null &&
            (poison.TriggerHpPercent is >= 1 and <= 99 && poison.FirstActivationRound == 0 && poison.CycleRounds == 0 ||
                poison.TriggerHpPercent is null && poison.FirstActivationRound >= 1 &&
                poison.CycleRounds >= poison.WindowRounds + poison.RewardRounds && poison.ErosionStartStacks > 0) &&
            poison.WindowRounds is >= 2 and <= 10 && poison.RewardRounds is >= 1 and <= 10 &&
            poison.BreakLightDamagePercent is > 0 and <= 100 && poison.AttackPercentPerStack is > 0 and <= 1000 &&
            codes.All(code => !string.IsNullOrWhiteSpace(code)) && codes.Distinct(StringComparer.OrdinalIgnoreCase).Count() == codes.Length &&
            Statuses.Find(poison.PoisonStatusCode) is { EffectType: "DamageOverTime", IsPositive: false, IsDispellable: true,
                IsHidden: false, Lifetime: Game.Shared.Enums.BattleStatusLifetime.Rounds,
                Mechanic: Game.Shared.Enums.BattleStatusMechanic.PlaguePoison,
                CounterKind: Game.Shared.Enums.BattleStatusCounterKind.Stacks, InitialStacks: 1 } dot &&
            dot.MaxStacks == (poison.LethalStacks > 0 ? poison.LethalStacks : poison.WindowRounds) && ValidPlagueLink(poison, codes) &&
            ValidColdMarker(poison.TargetStatusCode, Game.Shared.Enums.BattleStatusLifetime.Encounter) &&
            ValidColdMarker(poison.TickUsedStatusCode, Game.Shared.Enums.BattleStatusLifetime.Encounter) &&
            Statuses.Find(poison.TickUsedStatusCode)!.MaxStacks >= poison.WindowRounds &&
            Statuses.Find(poison.RewardStatusCode) is { EffectType: "ReductionPercent", ValuePerStack: > 0 and < 100,
                IsPositive: true, IsDispellable: false, IsHidden: false, MaxStacks: 1,
                Lifetime: Game.Shared.Enums.BattleStatusLifetime.Rounds } &&
            poison.BasicPoisonStatusCodes.Count > 0 && poison.BasicPoisonStatusCodes.Distinct(StringComparer.OrdinalIgnoreCase).Count() == poison.BasicPoisonStatusCodes.Count &&
            poison.BasicPoisonStatusCodes.All(code => !codes.Contains(code) &&
                Statuses.Find(code) is { EffectType: "DamageOverTime", IsPositive: false, IsDispellable: true,
                    Mechanic: Game.Shared.Enums.BattleStatusMechanic.None });
    }

    private bool ValidPlagueLink(PlaguePoisonOptions poison, string[] codes) => poison.ErosionStartStacks == 0
        ? poison.ErosionPercentPerStack == 0 && poison.LethalStacks == 0 && string.IsNullOrEmpty(poison.ErosionStatusCode)
        : poison.ErosionStartStacks >= 2 && poison.ErosionStartStacks <= poison.WindowRounds &&
            poison.LethalStacks == poison.WindowRounds + 1 && poison.ErosionPercentPerStack > 0 &&
            poison.ErosionPercentPerStack * (poison.WindowRounds - poison.ErosionStartStacks + 1) < 100 &&
            !codes.Contains(poison.ErosionStatusCode, StringComparer.OrdinalIgnoreCase) &&
            !poison.BasicPoisonStatusCodes.Contains(poison.ErosionStatusCode, StringComparer.OrdinalIgnoreCase) &&
            Statuses.Find(poison.ErosionStatusCode) is { EffectType: "None", IsPositive: false, IsDispellable: false,
                IsHidden: false, Lifetime: Game.Shared.Enums.BattleStatusLifetime.Encounter,
                Mechanic: Game.Shared.Enums.BattleStatusMechanic.PlagueErosion,
                CounterKind: Game.Shared.Enums.BattleStatusCounterKind.Stacks, InitialStacks: 1 } erosion &&
            erosion.MaxStacks == poison.WindowRounds - poison.ErosionStartStacks + 1;

    private bool ValidDeepCold(MonsterCombatProfileOptions profile)
    {
        if (profile.DeepCold is not { } cold) return true;
        var validTrigger = cold.TriggerHpPercent is { } hp
            ? hp is >= 1 and <= 99 && cold.FirstActivationRound == 0 && cold.CycleRounds == 0
            : cold.FirstActivationRound is >= 1 and <= 250 && cold.CycleRounds is >= 1 and <= 250 &&
                cold.CycleRounds > cold.WindowRounds + cold.RewardRounds;
        var codes = new[] { cold.ColdStatusCode, cold.FieldStatusCode, cold.WarmStatusCode,
            cold.PendingStatusCode, cold.ClearedStatusCode, cold.MeltUsedStatusCode };
        return profile.FireCore is null && validTrigger && Enum.IsDefined(cold.RemovalElement) &&
            cold.WindowRounds is >= 1 and <= 10 && cold.RewardRounds is >= 1 and <= 10 &&
            cold.InitialStacks is >= 1 and <= 10 && ValidColdGrowth(cold, codes) &&
            codes.Distinct(StringComparer.OrdinalIgnoreCase).Count() == codes.Length &&
            Statuses.Find(cold.ColdStatusCode) is { EffectType: "AttackPercent", ValuePerStack: < 0, IsPositive: false,
                IsDispellable: true, Lifetime: Game.Shared.Enums.BattleStatusLifetime.Rounds } status &&
            status.MaxStacks >= cold.InitialStacks && status.InitialStacks == cold.InitialStacks &&
            Statuses.Find(cold.FieldStatusCode) is { EffectType: "None", IsPositive: true, IsDispellable: false,
                Lifetime: Game.Shared.Enums.BattleStatusLifetime.Rounds, IsHidden: false } &&
            Statuses.Find(cold.WarmStatusCode) is { EffectType: "DamageDealtPercent", ValuePerStack: > 0 and <= 100,
                IsPositive: true, IsDispellable: false, Lifetime: Game.Shared.Enums.BattleStatusLifetime.Rounds, IsHidden: false } &&
            ValidColdMarker(cold.PendingStatusCode, Game.Shared.Enums.BattleStatusLifetime.Rounds) &&
            ValidColdMarker(cold.ClearedStatusCode, Game.Shared.Enums.BattleStatusLifetime.Encounter) &&
            ValidColdMarker(cold.MeltUsedStatusCode, Game.Shared.Enums.BattleStatusLifetime.CurrentRound) &&
            cold.BasicColdStatusCodes.Count > 0 && cold.BasicColdStatusCodes.Distinct().Count() == cold.BasicColdStatusCodes.Count &&
            cold.BasicColdStatusCodes.All(code => !codes.Contains(code) &&
                Statuses.Find(code) is { EffectType: "AttackPercent", ValuePerStack: < 0, IsPositive: false });
    }

    private bool ValidEarthArmor(MonsterCombatProfileOptions profile)
    {
        if (profile.EarthArmor is not { } armor) return true;
        var validTrigger = armor.TriggerHpPercent is { } hp
            ? hp is >= 1 and <= 99 && armor.FirstActivationRound == 0 && armor.CycleRounds == 0
            : armor.FirstActivationRound is >= 1 and <= 250 && armor.CycleRounds is >= 1 and <= 250 &&
                armor.CycleRounds >= armor.WindowRounds + armor.RewardRounds;
        return profile.FireCore is null && profile.DeepCold is null &&
            validTrigger &&
            armor.WindowRounds is >= 1 and <= 10 && armor.RewardRounds is >= 1 and <= 10 &&
            armor.BreakWindDamagePercent is > 0 and <= 100 &&
            !string.Equals(armor.ArmorStatusCode, armor.RewardStatusCode, StringComparison.OrdinalIgnoreCase) &&
            Statuses.Find(armor.ArmorStatusCode) is { EffectType: "ReductionPercent", IsDispellable: false,
                IsPositive: true, MaxStacks: 1, Lifetime: Game.Shared.Enums.BattleStatusLifetime.Rounds,
                ValuePerStack: > 0 and < 100 } &&
            Statuses.Find(armor.RewardStatusCode) is { EffectType: "DamageTakenPercent", IsDispellable: false,
                IsPositive: false, MaxStacks: 1, Lifetime: Game.Shared.Enums.BattleStatusLifetime.Rounds,
                ValuePerStack: > 0 and <= 100 } && ValidEarthResonance(armor);
    }

    private bool ValidStaticField(MonsterCombatProfileOptions profile)
    {
        if (profile.StaticField is not { } field) return true;
        var validTrigger = field.TriggerHpPercent is { } hp
            ? hp is >= 1 and <= 99 && field.FirstActivationRound == 0 && field.CycleRounds == 0
            : field.FirstActivationRound is >= 1 and <= 250 && field.CycleRounds is >= 1 and <= 250 &&
                field.CycleRounds >= field.WindowRounds + field.RewardRounds;
        var codes = new[] { field.StaticStatusCode, field.HitUsedStatusCode, field.GrowthUsedStatusCode, field.RewardStatusCode };
        return profile.FireCore is null && profile.DeepCold is null && profile.EarthArmor is null &&
            validTrigger && Enum.IsDefined(field.RemovalElement) &&
            field.WindowRounds is >= 2 and <= 10 && field.RewardRounds is >= 1 and <= 10 &&
            field.GrowthRounds > 0 && field.GrowthRounds < field.WindowRounds &&
            field.GrowthStacksPerRound is >= 1 and <= 10 && field.InitialStacks is >= 1 and <= 10 &&
            field.SkillDamagePercentPerStack is > 0 and <= 100 &&
            codes.Distinct(StringComparer.OrdinalIgnoreCase).Count() == codes.Length &&
            profile.Skills.Any(s => s.Code == field.AmplifiedSkillCode) &&
            _skills.TryGetValue(field.AmplifiedSkillCode, out var linked) && linked.TargetType == "AllAlive" &&
            linked.DamagePowerPercent > 0 &&
            Statuses.Find(field.StaticStatusCode) is { EffectType: "None", IsPositive: true, IsDispellable: false,
                IsHidden: false, Lifetime: Game.Shared.Enums.BattleStatusLifetime.Rounds,
                CounterKind: Game.Shared.Enums.BattleStatusCounterKind.Stacks } counter &&
            counter.MaxStacks >= field.InitialStacks && counter.InitialStacks == field.InitialStacks && ValidStaticThunder(profile, field, counter.MaxStacks) &&
            ValidColdMarker(field.HitUsedStatusCode, Game.Shared.Enums.BattleStatusLifetime.CurrentRound) &&
            ValidColdMarker(field.GrowthUsedStatusCode, Game.Shared.Enums.BattleStatusLifetime.Encounter) &&
            Statuses.Find(field.GrowthUsedStatusCode)!.MaxStacks >= field.GrowthRounds &&
            Statuses.Find(field.RewardStatusCode) is { EffectType: "DamageTakenPercent", IsDispellable: false,
                IsPositive: false, IsHidden: false, MaxStacks: 1, Lifetime: Game.Shared.Enums.BattleStatusLifetime.Rounds,
                ValuePerStack: > 0 and <= 100 };
    }

    private bool ValidReflectionMirror(MonsterCombatProfileOptions profile)
    {
        if (profile.ReflectionMirror is not { } mirror) return true;
        var validTrigger = mirror.TriggerHpPercent is { } hp
            ? hp is >= 1 and <= 99 && mirror.FirstActivationRound == 0 && mirror.CycleRounds == 0
            : mirror.FirstActivationRound is >= 1 and <= 250 && mirror.CycleRounds is >= 1 and <= 250 &&
                mirror.CycleRounds >= mirror.WindowRounds + mirror.RewardRounds;
        var codes = new[] { mirror.MirrorStatusCode, mirror.HitUsedStatusCode, mirror.BudgetStatusCode, mirror.RewardStatusCode };
        return profile.FireCore is null && profile.DeepCold is null && profile.EarthArmor is null && profile.StaticField is null &&
            validTrigger && mirror.WindowRounds is >= 1 and <= 10 &&
            mirror.InitialStacks is >= 1 and <= 10 && Enum.IsDefined(mirror.RemovalElement) &&
            mirror.ReflectPercentPerStack is > 0 and <= 100 && mirror.ReflectGrowthPercentPerStack is >= 0 and <= 100 &&
            mirror.MaxHpCapPercentPerStack is > 0 and <= 100 && mirror.MaxHpCapGrowthPercentPerStack is >= 0 and <= 100 &&
            (mirror.ReflectPercentPerStack + mirror.GrowthRounds * mirror.ReflectGrowthPercentPerStack) * mirror.InitialStacks <= 100 &&
            (mirror.MaxHpCapPercentPerStack + mirror.GrowthRounds * mirror.MaxHpCapGrowthPercentPerStack) * mirror.InitialStacks <= 100 &&
            ValidMirrorAmplification(mirror, codes) &&
            mirror.RewardRounds is >= 1 and <= 10 && codes.Distinct(StringComparer.OrdinalIgnoreCase).Count() == codes.Length &&
            Statuses.Find(mirror.MirrorStatusCode) is { EffectType: "None", IsPositive: true, IsDispellable: true,
                IsHidden: false, Lifetime: Game.Shared.Enums.BattleStatusLifetime.Rounds,
                CounterKind: Game.Shared.Enums.BattleStatusCounterKind.Stacks } counter &&
            counter.InitialStacks == mirror.InitialStacks && counter.MaxStacks == mirror.InitialStacks &&
            ValidColdMarker(mirror.HitUsedStatusCode, Game.Shared.Enums.BattleStatusLifetime.CurrentRound) &&
            ValidColdMarker(mirror.BudgetStatusCode, Game.Shared.Enums.BattleStatusLifetime.CurrentRound) &&
            Statuses.Find(mirror.RewardStatusCode) is { EffectType: "DamageTakenPercent", ValuePerStack: > 0 and <= 100,
                IsPositive: false, IsDispellable: false, IsHidden: false, MaxStacks: 1,
                Lifetime: Game.Shared.Enums.BattleStatusLifetime.Rounds };
    }

    private bool ValidMirrorAmplification(ReflectionMirrorOptions mirror, string[] codes) =>
        mirror.GrowthRounds == 0
            ? mirror.ReflectGrowthPercentPerStack == 0 && mirror.MaxHpCapGrowthPercentPerStack == 0 &&
              string.IsNullOrEmpty(mirror.AmplificationStatusCode)
            : mirror.GrowthRounds > 0 && mirror.GrowthRounds < mirror.WindowRounds &&
              mirror.ReflectGrowthPercentPerStack > 0 && mirror.MaxHpCapGrowthPercentPerStack > 0 &&
              !codes.Contains(mirror.AmplificationStatusCode, StringComparer.OrdinalIgnoreCase) &&
              Statuses.Find(mirror.AmplificationStatusCode) is { EffectType: "None", IsPositive: true, IsDispellable: false,
                  IsHidden: false, InitialStacks: 1, CounterKind: Game.Shared.Enums.BattleStatusCounterKind.Stacks,
                  Lifetime: Game.Shared.Enums.BattleStatusLifetime.Rounds } amplifier && amplifier.MaxStacks == mirror.GrowthRounds;

    private bool ValidStaticThunder(MonsterCombatProfileOptions profile, StaticFieldOptions field, int maxStacks) =>
        field.ThunderAtStacks == 0
            ? string.IsNullOrEmpty(field.ThunderSkillCode) && string.IsNullOrEmpty(field.ThunderPendingStatusCode)
            : field.ThunderAtStacks == maxStacks && field.ThunderAtStacks > field.InitialStacks &&
              profile.Skills.Any(s => s.Code == field.ThunderSkillCode) &&
              _skills.TryGetValue(field.ThunderSkillCode, out var thunder) && thunder.TargetType == "AllAlive" &&
              thunder.DamagePowerPercent > 0 && !thunder.IsInterruptible && thunder.DangerLevel == "Deadly" &&
              !new[] { field.StaticStatusCode, field.HitUsedStatusCode, field.GrowthUsedStatusCode, field.RewardStatusCode }
                  .Contains(field.ThunderPendingStatusCode, StringComparer.OrdinalIgnoreCase) &&
              Statuses.Find(field.ThunderPendingStatusCode) is { EffectType: "None", IsPositive: true,
                  IsDispellable: false, IsHidden: false, MaxStacks: 1, Lifetime: Game.Shared.Enums.BattleStatusLifetime.Rounds };

    private bool ValidEarthResonance(EarthArmorOptions armor) =>
        string.IsNullOrEmpty(armor.ResonanceStatusCode) ||
        !string.Equals(armor.ResonanceStatusCode, armor.ArmorStatusCode, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(armor.ResonanceStatusCode, armor.RewardStatusCode, StringComparison.OrdinalIgnoreCase) &&
        Statuses.Find(armor.ResonanceStatusCode) is { EffectType: "AttackPercent", IsPositive: true,
            IsDispellable: false, IsHidden: false, InitialStacks: 1, ValuePerStack: > 0 and <= 100,
            CounterKind: Game.Shared.Enums.BattleStatusCounterKind.Stacks,
            Lifetime: Game.Shared.Enums.BattleStatusLifetime.Encounter };

    private bool ValidColdMarker(string code, Game.Shared.Enums.BattleStatusLifetime lifetime) =>
        Statuses.Find(code) is { EffectType: "None", IsPositive: true, IsDispellable: false, IsHidden: true } marker &&
        marker.Lifetime == lifetime;

    private bool ValidColdGrowth(DeepColdOptions cold, string[] baseCodes)
    {
        var codes = new[] { cold.FreezeStatusCode, cold.FreezePendingStatusCode, cold.FreezeUsedStatusCode, cold.GrowthUsedStatusCode };
        if (cold.GrowthStacksPerRound == 0)
            return cold.FreezeAtStacks == 0 && codes.All(string.IsNullOrEmpty);
        return cold.GrowthStacksPerRound is >= 1 and <= 10 && cold.FreezeAtStacks > cold.InitialStacks &&
            Statuses.Find(cold.ColdStatusCode)?.MaxStacks == cold.FreezeAtStacks &&
            codes.Concat(baseCodes).Distinct(StringComparer.OrdinalIgnoreCase).Count() == codes.Length + baseCodes.Length &&
            Statuses.Find(cold.FreezeStatusCode) is { EffectType: "ActionBlocked", ValuePerStack: 1, MaxStacks: 1,
                IsPositive: false, IsDispellable: false, IsHidden: false, Lifetime: Game.Shared.Enums.BattleStatusLifetime.CurrentRound } &&
            ValidColdMarker(cold.FreezePendingStatusCode, Game.Shared.Enums.BattleStatusLifetime.Rounds) &&
            ValidColdMarker(cold.FreezeUsedStatusCode, Game.Shared.Enums.BattleStatusLifetime.Encounter) &&
            ValidColdMarker(cold.GrowthUsedStatusCode, Game.Shared.Enums.BattleStatusLifetime.CurrentRound);
    }

    public string ResolveDepthProfile(string baseProfileCode, int depth)
    {
        if (depth <= 1 || !_depthStages.TryGetValue(baseProfileCode, out var stages)) return baseProfileCode;
        var stage = stages.LastOrDefault(level => level <= depth);
        return stage == 0 ? baseProfileCode : DepthProfileCode(baseProfileCode, stage);
    }

    public IReadOnlyList<string> GetAddedMechanics(string baseProfileCode, int depth)
    {
        if (!_profiles.TryGetValue(baseProfileCode, out var original)) return [];
        var resolved = ResolveProfile(ResolveDepthProfile(baseProfileCode, depth));
        var originalCodes = original.Skills.Select(skill => skill.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = resolved is null ? [] : resolved.Skills.Where(skill => !originalCodes.Contains(skill.Code) &&
                skill.Code != FindProfile(ResolveDepthProfile(baseProfileCode, depth))?.StaticField?.ThunderSkillCode)
            .Select(skill => _definitions[skill.Code].Name).Distinct().ToList();
        if (FindProfile(ResolveDepthProfile(baseProfileCode, depth))?.FireCore is { } core)
        {
            names.Add(Statuses.Find(core.HeatingStatusCode)!.Name);
            if (core.ExtraTargetCount > 0) names.Add("熔火扩散");
            if (core.TriggerHpPercent is null) names.Add("火山循环");
        }
        if (FindProfile(ResolveDepthProfile(baseProfileCode, depth))?.DeepCold is { } cold)
        {
            names.Add(Statuses.Find(cold.ColdStatusCode)!.Name);
            if (cold.FreezeAtStacks > 0) names.Add(Statuses.Find(cold.FreezeStatusCode)!.Name);
            if (cold.TriggerHpPercent is null) names.Add("永冻循环");
        }
        if (FindProfile(ResolveDepthProfile(baseProfileCode, depth))?.EarthArmor is { } armor)
        {
            names.Add(Statuses.Find(armor.ArmorStatusCode)!.Name);
            if (!string.IsNullOrEmpty(armor.ResonanceStatusCode)) names.Add(Statuses.Find(armor.ResonanceStatusCode)!.Name);
            if (armor.TriggerHpPercent is null) names.Add("深岩循环");
        }
        if (FindProfile(ResolveDepthProfile(baseProfileCode, depth))?.StaticField is { } field)
        {
            names.Add("静电领域");
            if (field.ThunderAtStacks > 0) names.Add("雷暴共鸣");
            if (field.TriggerHpPercent is null) names.Add("风暴循环");
        }
        if (FindProfile(ResolveDepthProfile(baseProfileCode, depth))?.ReflectionMirror is { } mirror)
        {
            names.Add("琉辉反镜");
            if (mirror.GrowthRounds > 0) names.Add(Statuses.Find(mirror.AmplificationStatusCode)!.Name);
            if (mirror.TriggerHpPercent is null) names.Add("黎明循环");
        }
        if (FindProfile(ResolveDepthProfile(baseProfileCode, depth))?.PlaguePoison is { } poison)
        {
            names.Add(Statuses.Find(poison.PoisonStatusCode)!.Name);
            if (poison.ErosionStartStacks > 0) names.Add(Statuses.Find(poison.ErosionStatusCode)!.Name);
            if (poison.LethalStacks > 0) names.Add("腐巢死亡倒计时");
            if (poison.TriggerHpPercent is null) names.Add("瘟疫循环");
        }
        return names.Distinct().ToArray();
    }

    // Export authored declarations, never the generated profiles, so rebuilding cannot generate twice.
    public MonsterCombatOptions ExportOptions() => Copy(_source);

    private static MonsterCombatOptions Copy(MonsterCombatOptions source)
    {
        var copy = JsonSerializer.Deserialize<MonsterCombatOptions>(JsonSerializer.Serialize(source))!;
        copy.Profiles = new(copy.Profiles, StringComparer.OrdinalIgnoreCase);
        copy.DepthProgressions = new(copy.DepthProgressions, StringComparer.OrdinalIgnoreCase);
        return copy;
    }

    private static MonsterCombatProfileOptions CopyProfile(MonsterCombatProfileOptions source) => new()
    {
        SkillUseChancePercent = source.SkillUseChancePercent,
        UseEncounterLocalSkillClock = source.UseEncounterLocalSkillClock,
        FireCore = source.FireCore is null ? null : JsonSerializer.Deserialize<FireCoreOptions>(JsonSerializer.Serialize(source.FireCore)),
        DeepCold = source.DeepCold is null ? null : JsonSerializer.Deserialize<DeepColdOptions>(JsonSerializer.Serialize(source.DeepCold)),
        EarthArmor = source.EarthArmor is null ? null : JsonSerializer.Deserialize<EarthArmorOptions>(JsonSerializer.Serialize(source.EarthArmor)),
        StaticField = source.StaticField is null ? null : JsonSerializer.Deserialize<StaticFieldOptions>(JsonSerializer.Serialize(source.StaticField)),
        ReflectionMirror = source.ReflectionMirror is null ? null : JsonSerializer.Deserialize<ReflectionMirrorOptions>(JsonSerializer.Serialize(source.ReflectionMirror)),
        PlaguePoison = source.PlaguePoison is null ? null : JsonSerializer.Deserialize<PlaguePoisonOptions>(JsonSerializer.Serialize(source.PlaguePoison)),
        Skills = source.Skills.Select(skill => new MonsterProfileSkillOptions { Code = skill.Code, Weight = skill.Weight }).ToList()
    };

    private static string DepthProfileCode(string baseProfileCode, int depth) => $"{baseProfileCode}:depth-lv{depth}";

    public BattleStatusDefinition? FindStatus(string? code) => Statuses.Find(code);

    public MonsterSkillDefinition? ResolveSkill(string? code) =>
        code is not null && _definitions.TryGetValue(code, out var skill) ? skill : null;

    public MonsterCombatProfile? ResolveProfile(string? code) =>
        code is not null && _compiledProfiles.TryGetValue(code, out var profile) ? profile : null;

    public MonsterSkillOptions? FindSkill(string? code) =>
        code is not null && _skills.TryGetValue(code, out var skill) ? skill : null;

    public MonsterCombatProfileOptions? FindProfile(string? code) =>
        code is not null && _profiles.TryGetValue(code, out var profile) ? profile : null;
}
