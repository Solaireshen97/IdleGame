using System.Text.RegularExpressions;
using Game.Server.Configuration;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class DungeonDepthCatalog
{
    private readonly Dictionary<string, DungeonDepthDefinitionOptions> _dungeons = new(StringComparer.OrdinalIgnoreCase);

    public DungeonDepthCatalog(IOptions<DungeonDepthOptions> options)
    {
        foreach (var (code, definition) in options.Value.Dungeons)
        {
            if (string.IsNullOrWhiteSpace(code) || definition.Revision < 1 || definition.Stage < 1 ||
                definition.MaximumDepth is < 1 or > 100 || definition.GrowthPercent < 0 ||
                definition.ChallengeHpGrowthPercent is < 0 || definition.ChallengeAttackGrowthPercent is < 0 ||
                definition.CalibratedDepths.Any(depth => depth < 1 || depth > definition.MaximumDepth) ||
                definition.CalibratedDepths.Distinct().Count() != definition.CalibratedDepths.Count ||
                definition.GoldBonusPercent < 0 ||
                definition.KillExtraRollChancePercent is < 0 or > 100 ||
                definition.ClearExtraRollChancePercent is < 0 or > 100 ||
                definition.ChallengeFragmentChancePercent is < 0 or > 100 ||
                definition.ChallengeStartDepth < 5 || definition.ChallengeFragmentQuantity < 1 ||
                definition.ChallengeFragmentQuantities.Any(item => item.Key < definition.ChallengeStartDepth || item.Key > definition.MaximumDepth || item.Value < 1) ||
                definition.ChallengeFirstClearQuantities.Any(item => item.Key < definition.ChallengeStartDepth || item.Key > definition.MaximumDepth || item.Value < 1) ||
                definition.ChallengeFirstClearQuantities.Count > 0 && string.IsNullOrWhiteSpace(definition.ChallengeFirstClearItemCode) ||
                string.IsNullOrWhiteSpace(definition.ChallengeFragmentCode) || !_dungeons.TryAdd(code, definition))
                throw new InvalidOperationException($"Invalid dungeon depth configuration: {code}");
            try
            {
                _ = StatMultiplier(code, definition.MaximumDepth);
                _ = AttackMultiplier(code, definition.MaximumDepth);
            }
            catch (OverflowException exception)
            {
                throw new InvalidOperationException(
                    $"Invalid dungeon depth configuration: {code}; the LV{definition.MaximumDepth} stat multiplier exceeds the decimal range.", exception);
            }
        }
    }

    public DungeonDepthDefinitionOptions? Find(string code) => _dungeons.GetValueOrDefault(code);
    public IReadOnlyCollection<string> DungeonCodes => _dungeons.Keys;

    public bool ValidateDepth(string code, int depth) => depth >= 1 && depth <= (Find(code)?.MaximumDepth ?? 1);

    public decimal StatMultiplier(string code, int depth, int baseDepth = 1) =>
        Multiplier(code, depth, baseDepth, Find(code)?.ChallengeHpGrowthPercent);

    public decimal AttackMultiplier(string code, int depth, int baseDepth = 1) =>
        Multiplier(code, depth, baseDepth, Find(code)?.ChallengeAttackGrowthPercent);

    private decimal Multiplier(string code, int depth, int baseDepth, decimal? challengeGrowth)
    {
        if (!ValidateDepth(code, depth)) throw new ArgumentOutOfRangeException(nameof(depth));
        if (baseDepth < 1 || baseDepth > depth) throw new ArgumentOutOfRangeException(nameof(baseDepth));
        var definition = Find(code);
        var regularGrowth = definition?.GrowthPercent ?? 0m;
        var regularSteps = Math.Max(0, Math.Min(depth, (definition?.ChallengeStartDepth ?? 5) - 1) - baseDepth);
        var challengeSteps = depth - baseDepth - regularSteps;
        return checked(Power(1m + regularGrowth / 100m, regularSteps) *
            Power(1m + (challengeGrowth ?? regularGrowth) / 100m, challengeSteps));
    }

    private static decimal Power(decimal factor, int exponent)
    {
        var multiplier = 1m;
        // Bounded work also rejects enormous configured depths without an unbounded startup loop.
        while (exponent > 0)
        {
            if ((exponent & 1) != 0) multiplier = checked(multiplier * factor);
            exponent >>= 1;
            if (exponent > 0) factor = checked(factor * factor);
        }
        return multiplier;
    }

    public void ValidateStats(string code, int maxHp, int attack, string monsterName)
    {
        if (Find(code) is not { } definition) return;
        try
        {
            _ = ScaleStat(maxHp, definition.MaximumDepth, code);
            _ = ScaleAttack(attack, definition.MaximumDepth, code);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                $"Invalid dungeon depth configuration: {code}; monster {monsterName} HP or attack at LV{definition.MaximumDepth} exceeds the 32-bit stat range.", exception);
        }
    }

    public int ScaleStat(int baseValue, int depth, string code, int baseDepth = 1)
    {
        if (baseValue < 0) throw new ArgumentOutOfRangeException(nameof(baseValue));
        return checked((int)decimal.Ceiling(baseValue * StatMultiplier(code, depth, baseDepth)));
    }

    public int ScaleAttack(int baseValue, int depth, string code, int baseDepth = 1)
    {
        if (baseValue < 0) throw new ArgumentOutOfRangeException(nameof(baseValue));
        return checked((int)decimal.Ceiling(baseValue * AttackMultiplier(code, depth, baseDepth)));
    }

    public string DisplayName(string dungeonName, int depth)
    {
        if (depth < 1) throw new ArgumentOutOfRangeException(nameof(depth));
        return Regex.IsMatch(dungeonName, @"·深层\s*LV\d+$", RegexOptions.IgnoreCase)
            ? Regex.Replace(dungeonName, @"·深层\s*LV\d+$", $"·深层LV{depth}", RegexOptions.IgnoreCase)
            : $"{dungeonName}·深层LV{depth}";
    }

}
