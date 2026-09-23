namespace Game.Server.Configuration;

public sealed class ActivityOptions
{
    public const string SectionName = "Activities";
    public int MaximumHours { get; set; } = 12;
}
