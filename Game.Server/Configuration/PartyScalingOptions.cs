namespace Game.Server.Configuration;

public sealed class PartyScalingOptions
{
    public const string SectionName = "PartyScaling";
    public Dictionary<string, List<int>> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
