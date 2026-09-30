namespace Game.Shared.Enums;

public enum BattleStatusLifetime
{
    Rounds,
    UntilConsumed,
    Encounter,
    Run,
    CurrentRound
}

public enum BattleStatusCounterKind
{
    Stacks,
    Charges,
    None
}

public enum BattleStatusSnapshotRefresh
{
    KeepStronger,
    Replace
}

public enum BattleStatusFamilyRefresh { Replace, KeepStronger }

public enum BattleStatusMechanic
{
    None,
    ShadowCharges,
    MageDisorder,
    MageDomain,
    ContinuousDispel,
    NextDamageSkillReduction,
    HunterMark,
    HunterEagleEye,
    HunterVulnerability,
    HunterCoordinated,
    NextNativeHeal,
    NextNativeDamage,
    Revelation,
    Guard,
    GuardCounterattack,
    GuardCounterPermission,
    MageEchoUsed,
    NormalAttackEcho
}
