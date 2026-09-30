namespace Game.Server.Tests;

/// <summary>Locate read-only repository fixtures without assuming an MSBuild output layout.</summary>
internal static class TestRepository
{
    private static readonly Lazy<string> RepositoryRoot = new(() => FindRoot(AppContext.BaseDirectory));

    public static string Root => RepositoryRoot.Value;

    public static string File(params string[] segments) => Path.Combine([Root, .. segments]);

    internal static string FindRoot(string outputDirectory)
    {
        for (var candidate = new DirectoryInfo(Path.GetFullPath(outputDirectory)); candidate is not null;
             candidate = candidate.Parent)
        {
            if (System.IO.File.Exists(Path.Combine(candidate.FullName, "IdleGame.sln")) &&
                System.IO.File.Exists(Path.Combine(candidate.FullName, "Game.Server", "appsettings.json")))
                return candidate.FullName;
        }

        throw new DirectoryNotFoundException($"IdleGame repository fixtures not found above test output directory '{outputDirectory}'.");
    }
}
