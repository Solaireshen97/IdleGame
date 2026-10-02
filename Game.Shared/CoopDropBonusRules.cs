namespace Game.Shared;

public static class CoopDropBonusRules
{
    public static decimal CalculateBonusPercent(int distinctUserCount, decimal perAdditionalUserPercent, decimal maximumPercent)
    {
        if (perAdditionalUserPercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(perAdditionalUserPercent));
        if (maximumPercent is < 0 or > 400) throw new ArgumentOutOfRangeException(nameof(maximumPercent));
        return Math.Min(maximumPercent, (Math.Clamp(distinctUserCount, 1, 5) - 1) * perAdditionalUserPercent);
    }

    public static decimal ApplyToChance(decimal baseChancePercent, decimal bonusPercent)
    {
        if (baseChancePercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(baseChancePercent));
        if (bonusPercent is < 0 or > 400) throw new ArgumentOutOfRangeException(nameof(bonusPercent));
        return Math.Min(100m, baseChancePercent * (1m + bonusPercent / 100m));
    }
}
