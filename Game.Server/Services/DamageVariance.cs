namespace Game.Server.Services;

/// <summary>Applies one symmetric roll to resolved direct damage, followed by unbiased integer rounding.</summary>
public static class DamageVariance
{
    public static decimal ValidatePercent(decimal variancePercent)
    {
        if (variancePercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(variancePercent));
        return variancePercent;
    }

    public static int Roll(int damage, decimal variancePercent, Random random)
    {
        ValidatePercent(variancePercent);
        if (damage <= 0 || variancePercent == 0) return damage;
        var multiplier = 1m + ((decimal)random.NextDouble() * 2m - 1m) * variancePercent / 100m;
        var value = Math.Clamp(damage * multiplier, 1m, int.MaxValue);
        var lower = decimal.Floor(value);
        var fraction = value - lower;
        // A 9.7 result becomes 10 in 70% of cases. Flooring every hit would lower average damage,
        // while conventional rounding would erase small fluctuations on low-level attacks.
        return (int)lower + (fraction > 0m && (decimal)random.NextDouble() < fraction ? 1 : 0);
    }
}
