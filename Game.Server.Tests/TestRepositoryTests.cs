using Xunit;

namespace Game.Server.Tests;

public sealed class TestRepositoryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "idle-test-root-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Game.Server.Tests/bin/Release/net10.0")]
    [InlineData("artifacts/bin/Game.Server.Tests/release")]
    [InlineData(".codex-tmp/nested/build/bin/Game.Server.Tests/debug")]
    public void FindsValidatedRootForDifferentOutputLayouts(string relativeOutput)
    {
        Directory.CreateDirectory(Path.Combine(directory, "Game.Server"));
        System.IO.File.WriteAllText(Path.Combine(directory, "IdleGame.sln"), "test solution marker");
        System.IO.File.WriteAllText(Path.Combine(directory, "Game.Server", "appsettings.json"), "{}");
        var output = Path.Combine(directory, relativeOutput);
        Directory.CreateDirectory(output);
        // A partial lookalike under the artifacts directory must not become the fixture root.
        Directory.CreateDirectory(Path.Combine(output, "Game.Server"));
        System.IO.File.WriteAllText(Path.Combine(output, "Game.Server", "appsettings.json"), "{}");
        Assert.Equal(directory, TestRepository.FindRoot(output));
    }

    [Fact]
    public void RejectsAnUnrelatedDirectoryWithOnlyAConfigurationFile()
    {
        Directory.CreateDirectory(Path.Combine(directory, "Game.Server"));
        System.IO.File.WriteAllText(Path.Combine(directory, "Game.Server", "appsettings.json"), "{}");
        Assert.Throws<DirectoryNotFoundException>(() => TestRepository.FindRoot(directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
