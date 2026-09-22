namespace Game.Server.Configuration;

public sealed class ProgressionOptions
{
    public const string SectionName = "Progression";

    public int MaximumLevel { get; set; }
    public List<int> ExperienceToNextLevel { get; set; } = [];
    public List<int> ExperiencePercentByLevelDifference { get; set; } = [];
}
