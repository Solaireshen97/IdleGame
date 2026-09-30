using System.Text.Json;

namespace Game.Maintenance;

public static class MaintenanceApplication
{
    private const string Usage = """
        IdleGame SQLite maintenance (.NET 10)
        backup --source <absolute existing database> --target <absolute NEW database>
        verify --source <absolute existing database>
        restore --source <absolute standalone backup> --target <absolute NEW database> --service-stopped

        Target parent directory must already exist. Existing files and SQLite sidecars
        are never overwritten. Source/target paths through symbolic links or junctions
        are rejected. Backup supports an online SQLite source, including WAL commits.
        Stop the game before restore; --service-stopped acknowledges that manual step.
        Restore creates a new file only. Changing the service database path is manual.
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            if (args.Length == 1 && args[0] is "--help" or "-h")
            {
                output.WriteLine(Usage);
                return 0;
            }

            var command = Parse(args);
            var report = command.Operation switch
            {
                "verify" => DatabaseMaintenance.Verify(command.Source),
                "backup" => DatabaseMaintenance.Copy(command.Source, command.Target!, restore: false),
                "restore" => DatabaseMaintenance.Copy(command.Source, command.Target!, restore: true),
                _ => throw new ArgumentException("Unknown operation.")
            };
            output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            error.WriteLine($"Maintenance failed: {exception.Message}");
            return 1;
        }
    }

    private static Command Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("backup" or "verify" or "restore"))
            throw new ArgumentException(Usage);

        string? source = null;
        string? target = null;
        var stopped = false;
        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--source" when source is null && index + 1 < args.Length:
                    source = args[++index];
                    break;
                case "--target" when target is null && index + 1 < args.Length:
                    target = args[++index];
                    break;
                case "--service-stopped" when !stopped:
                    stopped = true;
                    break;
                default:
                    throw new ArgumentException("Unknown, duplicate, or incomplete argument. Use --help.");
            }
        }

        if (source is null || (args[0] != "verify" && target is null))
            throw new ArgumentException("--source is required; backup and restore also require --target.");
        if (args[0] == "verify" && (target is not null || stopped))
            throw new ArgumentException("verify accepts only --source.");
        if (args[0] == "backup" && stopped)
            throw new ArgumentException("--service-stopped applies only to restore.");
        if (args[0] == "restore" && !stopped)
            throw new ArgumentException("Stop the game service first, then pass --service-stopped to acknowledge it.");
        return new(args[0], source, target);
    }

    private sealed record Command(string Operation, string Source, string? Target);
}
