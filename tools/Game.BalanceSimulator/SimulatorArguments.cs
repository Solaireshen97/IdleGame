using System.Globalization;

namespace Game.BalanceSimulator;

public sealed class SimulatorArguments
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "--config", "--world", "--dungeon-code", "--depth", "--mastery", "--starting-potions",
        "--talent-builds", "--runs", "--seed-start", "--elements", "--weapon-element", "--roles",
        "--stages", "--targets", "--composition", "--party", "--soul-loadouts", "--mode", "--output"
    };
    private readonly Dictionary<string, string> _values;
    private SimulatorArguments(Dictionary<string, string> values) => _values = values;
    public string? DungeonCode => _values.GetValueOrDefault("--dungeon-code");
    public int Depth => Number("--depth", 1, 1, 100);
    public int Mastery => Number("--mastery", 0, 0, 4);
    public int StartingPotions => Number("--starting-potions", 1000, 0, 1_000_000);
    public string Value(string name, string fallback) => _values.GetValueOrDefault(name, fallback);
    public bool Contains(string name) => _values.ContainsKey(name);

    public static SimulatorArguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (name != "--trace" && !ValueOptions.Contains(name)) throw new ArgumentException($"Unknown option {name}");
            if (values.ContainsKey(name)) throw new ArgumentException($"Duplicate option {name}");
            if (name == "--trace") { values.Add(name, "true"); continue; }
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[index]))
                throw new ArgumentException($"Missing value for {name}");
            values.Add(name, args[index]);
        }
        var parsed = new SimulatorArguments(values);
        _ = parsed.Depth;
        _ = parsed.Mastery;
        _ = parsed.StartingPotions;
        if (parsed.DungeonCode is not null && parsed.Contains("--targets"))
            throw new ArgumentException("Use either --dungeon-code or --targets, not both.");
        return parsed;
    }

    private int Number(string name, int fallback, int minimum, int maximum)
    {
        if (!_values.TryGetValue(name, out var value)) return fallback;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < minimum || number > maximum)
            throw new ArgumentException($"{name} must be an integer from {minimum} to {maximum}");
        return number;
    }
}
