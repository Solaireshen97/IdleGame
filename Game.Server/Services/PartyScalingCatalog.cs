using Game.Server.Configuration;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class PartyScalingCatalog
{
    public const string FixedProfileCode = "fixed";
    public const string HuntProfileCode = "hunt-hp";
    private readonly Dictionary<string, int[]> _profiles = new(StringComparer.OrdinalIgnoreCase);

    public static PartyScalingCatalog Default { get; } = new(Options.Create(new PartyScalingOptions
    {
        Profiles = new(StringComparer.OrdinalIgnoreCase)
        {
            [FixedProfileCode] = [100, 100, 100, 100, 100],
            [HuntProfileCode] = [100, 180, 260, 340, 420]
        }
    }));

    public PartyScalingCatalog(IOptions<PartyScalingOptions> options)
    {
        foreach (var (code, percentages) in options.Value.Profiles)
        {
            if (string.IsNullOrWhiteSpace(code) || percentages is null || percentages.Count != 5 ||
                percentages[0] != 100 || percentages.Any(percent => percent < 100) ||
                percentages.Zip(percentages.Skip(1)).Any(pair => pair.Second < pair.First))
                throw new InvalidOperationException($"Invalid party scaling profile: {code}");
            _profiles.Add(code, percentages.ToArray());
        }
        if (!_profiles.TryGetValue(FixedProfileCode, out var fixedProfile) || fixedProfile.Any(percent => percent != 100))
            throw new InvalidOperationException("Party scaling must define a fixed profile with 100% HP for all party sizes.");
    }

    public bool HasProfile(string code) => !string.IsNullOrWhiteSpace(code) && _profiles.ContainsKey(code);

    public IReadOnlyList<int> GetHpPercentages(string code) =>
        _profiles.TryGetValue(code, out var percentages) ? percentages :
            throw new InvalidOperationException($"Unknown party scaling profile: {code}");

    public int GetHpPercent(string code, int partySize) => GetHpPercentages(code)[Math.Clamp(partySize, 1, 5) - 1];

    public bool ScalesHp(string code) => GetHpPercentages(code).Any(percent => percent != 100);
}
