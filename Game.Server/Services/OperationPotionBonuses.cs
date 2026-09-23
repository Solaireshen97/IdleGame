namespace Game.Server.Services;

public readonly record struct OperationPotionBonuses(
    int AttackPercent,
    int FinalDamagePercent,
    int DamageTakenPercent,
    int NormalAttackDamagePercent,
    int AreaDamageReductionPercent);
