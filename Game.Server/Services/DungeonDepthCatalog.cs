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
                definition.GoldBonusPercent < 0 ||
                definition.KillExtraRollChancePercent is < 0 or > 100 ||
                definition.ClearExtraRollChancePercent is < 0 or > 100 ||
                definition.ChallengeFragmentChancePercent is < 0 or > 100 ||
                definition.ChallengeStartDepth < 5 || definition.ChallengeFragmentQuantity < 1 ||
                string.IsNullOrWhiteSpace(definition.ChallengeFragmentCode) || !_dungeons.TryAdd(code, definition))
                throw new InvalidOperationException($"Invalid dungeon depth configuration: {code}");
            try
            {
                _ = StatMultiplier(code, definition.MaximumDepth);
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

    public decimal StatMultiplier(string code, int depth)
    {
        if (!ValidateDepth(code, depth)) throw new ArgumentOutOfRangeException(nameof(depth));
        var factor = 1m + (Find(code)?.GrowthPercent ?? 0m) / 100m;
        var multiplier = 1m;
        var exponent = depth - 1;
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
            _ = ScaleStat(attack, definition.MaximumDepth, code);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                $"Invalid dungeon depth configuration: {code}; monster {monsterName} HP or attack at LV{definition.MaximumDepth} exceeds the 32-bit stat range.", exception);
        }
    }

    public int ScaleStat(int baseValue, int depth, string code)
    {
        if (baseValue < 0) throw new ArgumentOutOfRangeException(nameof(baseValue));
        return checked((int)decimal.Ceiling(baseValue * StatMultiplier(code, depth)));
    }

    public string DisplayName(string dungeonName, int depth)
    {
        if (depth < 1) throw new ArgumentOutOfRangeException(nameof(depth));
        return Regex.IsMatch(dungeonName, @"·深层\s*LV\d+$", RegexOptions.IgnoreCase)
            ? Regex.Replace(dungeonName, @"·深层\s*LV\d+$", $"·深层LV{depth}", RegexOptions.IgnoreCase)
            : $"{dungeonName}·深层LV{depth}";
    }

}
