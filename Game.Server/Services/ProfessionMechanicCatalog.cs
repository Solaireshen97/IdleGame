using Game.Shared.Enums;

namespace Game.Server.Services;

/// <summary>Additional profession rules, independent of configured skill effects and multipliers.</summary>
public sealed class ProfessionMechanicCatalog
{
    public static ProfessionMechanicCatalog Default { get; } = new();
    public KnightMechanicDefinition Knight { get; }
    public RogueMechanicDefinition Rogue { get; }
    public HunterMechanicDefinition Hunter { get; }
    public MageMechanicDefinition Mage { get; }
    public AcolyteMechanicDefinition Acolyte { get; }
    public ThreeLevelValues<string> HunterCoordinatedCodes { get; } = new("hunter-coordinated-12", "hunter-coordinated-16", "hunter-coordinated-20");
    public ThreeLevelValues<string> HunterVulnerabilityCodes { get; } = new("hunter-vulnerability-8", "hunter-vulnerability-10", "hunter-vulnerability-12");
    public ThreeLevelValues<string> MageDisruptionCodes { get; } = new("mage-skill-disruption-15", "mage-skill-disruption-20", "mage-skill-disruption-25");

    public ProfessionMechanicCatalog(KnightMechanicDefinition? knight = null, RogueMechanicDefinition? rogue = null,
        HunterMechanicDefinition? hunter = null, MageMechanicDefinition? mage = null, AcolyteMechanicDefinition? acolyte = null)
    {
        Knight = knight ?? new();
        Rogue = rogue ?? new();
        Hunter = hunter ?? new(new(30, 35, 40), new(12, 16, 20), new(8, 10, 12));
        Mage = mage ?? new(new(15, 20, 25));
        Acolyte = acolyte ?? new(new(1, 2, 3));
        ValidateParameters();
    }

    public BattleStatusDefinition HunterCoordinatedStatus(BattleStatusCatalog statuses, int level) =>
        Require(statuses, HunterCoordinatedCodes.ForLevel(level),
            BattleStatusMechanic.HunterCoordinated, Hunter.MarkedCoordinatedPower.ForLevel(level));
    public BattleStatusDefinition HunterVulnerabilityStatus(BattleStatusCatalog statuses, int level) =>
        Require(statuses, HunterVulnerabilityCodes.ForLevel(level),
            BattleStatusMechanic.HunterVulnerability, Hunter.MarkedVulnerabilityPower.ForLevel(level));
    public BattleStatusDefinition MageDisruptionStatus(BattleStatusCatalog statuses, int domainRank) =>
        Require(statuses, MageDisruptionCodes.ForLevel(domainRank),
            BattleStatusMechanic.NextDamageSkillReduction,
            domainRank > 0 ? Mage.DisruptionPowerByDomainRank.ForLevel(domainRank) : Mage.BaseDisruptionPower);

    public BattleStatusDefinition? TryHunterCoordinatedStatus(BattleStatusCatalog statuses, int level) =>
        Resolve(statuses, HunterCoordinatedCodes.ForLevel(level), BattleStatusMechanic.HunterCoordinated, Hunter.MarkedCoordinatedPower.ForLevel(level));
    public BattleStatusDefinition? TryHunterVulnerabilityStatus(BattleStatusCatalog statuses, int level) =>
        Resolve(statuses, HunterVulnerabilityCodes.ForLevel(level), BattleStatusMechanic.HunterVulnerability, Hunter.MarkedVulnerabilityPower.ForLevel(level));
    public BattleStatusDefinition? TryMageDisruptionStatus(BattleStatusCatalog statuses, int rank) =>
        Resolve(statuses, MageDisruptionCodes.ForLevel(rank), BattleStatusMechanic.NextDamageSkillReduction,
            rank > 0 ? Mage.DisruptionPowerByDomainRank.ForLevel(rank) : Mage.BaseDisruptionPower);

    public void Validate(SkillCatalog skills, BattleStatusCatalog statuses)
    {
        ValidateParameters();
        foreach (var code in new[] { "sword-slash", "knight-faith-barrier", "knight-invigorate", "rogue-shadow-strike",
            "rogue-poisoned-blade", "rogue-adrenaline", "rogue-execution-slash", "hunter-precision-shot",
            "hunter-expose-shot", "hunter-hunting-signal", "mage-arcane-domain", "acolyte-purify", "acolyte-revelation" })
            if (skills.FindSkill(code) is null) throw new InvalidOperationException($"Missing profession mechanic skill: {code}");
        Require(statuses, BattleGuardService.GuardCode, BattleStatusMechanic.Guard);
        Require(statuses, BattleGuardService.PermissionCode, BattleStatusMechanic.GuardCounterPermission);
        Require(statuses, BattleGuardService.ReadyCode, BattleStatusMechanic.GuardCounterattack);
        Require(statuses, "rogue-shadow-charge", BattleStatusMechanic.ShadowCharges, minimumCapacity: Rogue.ChargeGain);
        Require(statuses, "hunter-prey-mark", BattleStatusMechanic.HunterMark);
        Require(statuses, "hunter-eagle-eye-1", BattleStatusMechanic.HunterEagleEye, minimumCapacity: Hunter.EagleEyeConsumption);
        Require(statuses, "hunter-eagle-eye-2", BattleStatusMechanic.HunterEagleEye, minimumCapacity: 2);
        Require(statuses, "mage-disorder", BattleStatusMechanic.MageDisorder, minimumCapacity: Mage.EchoRequiredStacks);
        Require(statuses, "mage-echo-used", BattleStatusMechanic.MageEchoUsed);
        Require(statuses, "mage-spellbreak-continuous", BattleStatusMechanic.ContinuousDispel);
        Require(statuses, "acolyte-next-damage", BattleStatusMechanic.NextNativeDamage, minimumCapacity: Acolyte.EnhancementCharges);
        Require(statuses, "acolyte-next-heal", BattleStatusMechanic.NextNativeHeal, minimumCapacity: Acolyte.EnhancementCharges);
        for (var level = 1; level <= 3; level++)
        {
            HunterCoordinatedStatus(statuses, level);
            HunterVulnerabilityStatus(statuses, level);
            MageDisruptionStatus(statuses, level);
            var domain = Require(statuses, $"mage-domain-{level}", BattleStatusMechanic.MageDomain);
            if (domain.MechanicLevel != level) throw new InvalidOperationException($"Invalid profession mechanic level: {domain.Code}");
            Require(statuses, "acolyte-revelation", BattleStatusMechanic.Revelation,
                minimumCapacity: Acolyte.RevelationChargesByLevel.ForLevel(level));
        }
        MageDisruptionStatus(statuses, 0);
    }

    private static BattleStatusDefinition Require(BattleStatusCatalog statuses, string code,
        BattleStatusMechanic mechanic, decimal? power = null, int minimumCapacity = 1)
    {
        var status = Resolve(statuses, code, mechanic, power);
        if (status is null || status.Mechanic != mechanic || power.HasValue && status.FamilyStrength != power ||
            status.MaxStacks < minimumCapacity || !CompatibleLifetimeAndCounter(status))
            throw new InvalidOperationException($"Missing or incompatible profession mechanic status: {code} ({mechanic}, power {power}, capacity {minimumCapacity})");
        return status;
    }

    private static bool CompatibleLifetimeAndCounter(BattleStatusDefinition status) => status.Mechanic switch
    {
        BattleStatusMechanic.ShadowCharges => status.Lifetime == BattleStatusLifetime.UntilConsumed && status.CounterKind == BattleStatusCounterKind.Stacks,
        BattleStatusMechanic.MageDisorder => status.Lifetime == BattleStatusLifetime.Encounter && status.CounterKind == BattleStatusCounterKind.Stacks,
        BattleStatusMechanic.NextNativeDamage or BattleStatusMechanic.NextNativeHeal or BattleStatusMechanic.Revelation or
            BattleStatusMechanic.NextDamageSkillReduction => status.Lifetime == BattleStatusLifetime.UntilConsumed && status.CounterKind == BattleStatusCounterKind.Charges,
        BattleStatusMechanic.HunterEagleEye => status.Lifetime == BattleStatusLifetime.Rounds && status.CounterKind == BattleStatusCounterKind.Charges,
        BattleStatusMechanic.GuardCounterPermission or BattleStatusMechanic.GuardCounterattack => status.Lifetime == BattleStatusLifetime.CurrentRound && status.CounterKind == BattleStatusCounterKind.Charges,
        BattleStatusMechanic.Guard or BattleStatusMechanic.MageEchoUsed => status.Lifetime == BattleStatusLifetime.CurrentRound,
        BattleStatusMechanic.HunterMark or BattleStatusMechanic.HunterCoordinated or BattleStatusMechanic.HunterVulnerability or
            BattleStatusMechanic.MageDomain or BattleStatusMechanic.ContinuousDispel => status.Lifetime == BattleStatusLifetime.Rounds,
        _ => true
    };

    private static BattleStatusDefinition? Resolve(BattleStatusCatalog statuses, string preferredCode,
        BattleStatusMechanic mechanic, decimal? power)
    {
        var preferred = statuses.Find(preferredCode);
        if (preferred is not null) return preferred.Mechanic == mechanic && (!power.HasValue || preferred.FamilyStrength == power) ? preferred : null;
        var candidates = statuses.Definitions.Where(status => status.Mechanic == mechanic &&
            (!power.HasValue || status.FamilyStrength == power)).Take(2).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private void ValidateParameters()
    {
        if (Knight.CounterAttackPowerPercent < 0 || Rogue.ChargeGain < 1 || Rogue.DamageBonusPerChargePercent < 0 ||
            Rogue.ExecuteBelowHpPercent is < 1 or > 100 || Rogue.ExecuteActualDamagePercent < 0 ||
            Hunter.VulnerabilityDurationRounds < 0 || Hunter.EagleEyeConsumption < 1 || Mage.DisorderGain < 1 ||
            Mage.DomainCastAdditionalDisorderGain < 0 || Mage.EchoRequiredStacks < 1 || Mage.EchoAttackPowerPercent < 0 ||
            Mage.DomainEchoAttackPowerPercent < 0 || Mage.BaseDisruptionPower is < 0 or > 100 ||
            Acolyte.DamageEnhancementPercent < 0 || Acolyte.HealingEnhancementPercent < 0 ||
            Acolyte.PurifySelfCleanseMinLevel is < 1 or > 3 || Acolyte.EnhancementCharges < 1 || Acolyte.RevelationConsumption < 1)
            throw new InvalidOperationException("Invalid profession mechanic parameters.");
        for (var level = 1; level <= 3; level++)
            if (Hunter.MarkedPrecisionAttackBonusPercent.ForLevel(level) < 0 || Hunter.MarkedCoordinatedPower.ForLevel(level) < 0 ||
                Hunter.MarkedVulnerabilityPower.ForLevel(level) < 0 || Mage.DisruptionPowerByDomainRank.ForLevel(level) is < 0 or > 100 ||
                Acolyte.RevelationChargesByLevel.ForLevel(level) < Acolyte.RevelationConsumption)
                throw new InvalidOperationException($"Invalid profession mechanic parameters at level {level}.");
    }
}
