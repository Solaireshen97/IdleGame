namespace Game.Server.Services;

public sealed record ThreeLevelValues<T>(T Level1, T Level2, T Level3)
{
    public T ForLevel(int level) => level == 3 ? Level3 : level == 2 ? Level2 : Level1;
}

public sealed record KnightMechanicDefinition(decimal CounterAttackPowerPercent = 30)
{
    public bool CanCounter(string code) => code is "sword-slash" or "knight-faith-barrier";
    public bool DeduplicatesHealing(string code) => code == "knight-invigorate";
}
public sealed record RogueMechanicDefinition(int ChargeGain = 1, decimal DamageBonusPerChargePercent = 20,
    int ExecuteBelowHpPercent = 35, decimal ExecuteActualDamagePercent = 30)
{
    public bool GeneratesCharge(string code) => code is "rogue-shadow-strike" or "rogue-poisoned-blade" or "rogue-adrenaline";
}
public sealed record HunterMechanicDefinition(ThreeLevelValues<decimal> MarkedPrecisionAttackBonusPercent,
    ThreeLevelValues<decimal> MarkedCoordinatedPower, ThreeLevelValues<decimal> MarkedVulnerabilityPower,
    int VulnerabilityDurationRounds = 1, int EagleEyeConsumption = 1);
public sealed record MageMechanicDefinition(ThreeLevelValues<decimal> DisruptionPowerByDomainRank,
    int DisorderGain = 1, int DomainCastAdditionalDisorderGain = 1, int EchoRequiredStacks = 3,
    decimal EchoAttackPowerPercent = 30, decimal DomainEchoAttackPowerPercent = 50, decimal BaseDisruptionPower = 15);
public sealed record AcolyteMechanicDefinition(ThreeLevelValues<int> RevelationChargesByLevel,
    decimal DamageEnhancementPercent = 15, decimal HealingEnhancementPercent = 20,
    int PurifySelfCleanseMinLevel = 2, int EnhancementCharges = 1, int RevelationConsumption = 1);
