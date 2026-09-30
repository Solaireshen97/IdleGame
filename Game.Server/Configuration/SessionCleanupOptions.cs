namespace Game.Server.Configuration;

public sealed class SessionCleanupOptions
{
    public const string SectionName = "SessionCleanup";
    public int IntervalMinutes { get; set; } = 60;
    public int BatchSize { get; set; } = 500;
}
