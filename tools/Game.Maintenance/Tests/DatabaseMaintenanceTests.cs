using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Game.Maintenance.Tests;

public sealed class DatabaseMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"IdleGameMaintenance-{Guid.NewGuid():N}");

    public DatabaseMaintenanceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void OnlineWalBackupAndOfflineRestorePreserveCommittedState()
    {
        var source = PathInRoot("live.db");
        using var live = Open(source);
        Execute(live, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE Saves (Id INTEGER PRIMARY KEY, Gold INTEGER NOT NULL); PRAGMA wal_checkpoint(TRUNCATE);");
        Execute(live, "INSERT INTO Saves VALUES (1, 123);");
        Assert.True(new FileInfo(source + "-wal").Length > 0);
        Execute(live, "BEGIN IMMEDIATE; UPDATE Saves SET Gold=999 WHERE Id=1;");

        var backup = PathInRoot("snapshot.db");
        var report = DatabaseMaintenance.Copy(source, backup, restore: false);
        Assert.Equal("backup", report.Operation);
        Assert.Equal(123L, ReadGold(backup));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(backup))).ToLowerInvariant(), report.Sha256);
        Assert.Equal(new FileInfo(backup).Length, report.Bytes);
        Assert.False(File.Exists(backup + "-wal"));
        Assert.False(File.Exists(backup + "-shm"));
        Execute(live, "ROLLBACK;");
        live.Close();

        var restored = PathInRoot("restored.db");
        Assert.Equal(0, Run("restore", "--source", backup, "--target", restored, "--service-stopped"));
        Assert.Equal(123L, ReadGold(restored));
        Assert.Equal(0, Run("verify", "--source", restored));
        Assert.Equal(123L, ReadGold(source));
    }

    [Theory]
    [InlineData("backup", "")]
    [InlineData("backup", "-wal")]
    [InlineData("backup", "-shm")]
    [InlineData("backup", "-journal")]
    [InlineData("restore", "")]
    [InlineData("restore", "-wal")]
    [InlineData("restore", "-shm")]
    [InlineData("restore", "-journal")]
    public void ExistingTargetAndSidecarsArePreserved(string operation, string suffix)
    {
        var source = CreateDatabase();
        var target = PathInRoot("occupied.db");
        var sentinel = new byte[] { 1, 5, 9, 12 };
        File.WriteAllBytes(target + suffix, sentinel);
        var arguments = new List<string> { operation, "--source", source, "--target", target };
        if (operation == "restore") arguments.Add("--service-stopped");
        Assert.Equal(1, Run(arguments.ToArray()));
        Assert.Equal(sentinel, File.ReadAllBytes(target + suffix));
        if (suffix != "") Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(_root, ".maintenance-*.db*"));
    }

    [Fact]
    public void MissingSourceIsNotCreatedAndSamePathIsNotOverwritten()
    {
        var missing = PathInRoot("missing.db");
        Assert.Equal(1, Run("backup", "--source", missing, "--target", PathInRoot("new.db")));
        Assert.False(File.Exists(missing));
        var source = CreateDatabase();
        var original = File.ReadAllBytes(source);
        Assert.Equal(1, Run("backup", "--source", source, "--target", source));
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Fact]
    public void RestoreRequiresStopAcknowledgementAndStandaloneSource()
    {
        var source = CreateDatabase();
        var target = PathInRoot("restored.db");
        Assert.Equal(1, Run("restore", "--source", source, "--target", target));
        Assert.False(File.Exists(target));
        File.WriteAllBytes(source + "-wal", [1, 2, 3]);
        Assert.Equal(1, Run("restore", "--source", source, "--target", target, "--service-stopped"));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void InvalidEmptyAndCorruptDatabasesFailWithoutPublishing()
    {
        var invalid = PathInRoot("invalid.db");
        File.WriteAllText(invalid, "This is not a save database.");
        var empty = PathInRoot("empty.db");
        File.WriteAllBytes(empty, []);
        var corrupt = CreateDatabase();
        var bytes = File.ReadAllBytes(corrupt);
        bytes[100] = 255; // Invalid b-tree page type, while retaining the SQLite file header.
        File.WriteAllBytes(corrupt, bytes);
        foreach (var source in new[] { invalid, empty, corrupt })
        {
            Assert.Equal(1, Run("verify", "--source", source));
            Assert.Equal(1, Run("backup", "--source", source, "--target", PathInRoot("not-published.db")));
            Assert.False(File.Exists(PathInRoot("not-published.db")));
        }
        Assert.Empty(Directory.GetFiles(_root, ".maintenance-*.db*"));
    }

    [Fact]
    public void ForeignKeyViolationsFailSnapshotVerification()
    {
        var source = CreateDatabase();
        using (var db = Open(source))
            Execute(db, "PRAGMA foreign_keys=OFF; CREATE TABLE Child (ParentId INTEGER REFERENCES Saves(Id)); INSERT INTO Child VALUES(999);");
        Assert.Equal(1, Run("verify", "--source", source));
        var target = PathInRoot("invalid-snapshot.db");
        Assert.Equal(1, Run("backup", "--source", source, "--target", target));
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(_root, ".maintenance-*.db*"));
    }

    [Fact]
    public void RelativePathsMissingParentsAndDirectoryTargetsAreRejected()
    {
        var source = CreateDatabase();
        Assert.Equal(1, Run("backup", "--source", source, "--target", "relative.db"));
        Assert.Equal(1, Run("verify", "--source", "relative.db"));
        Assert.Equal(1, Run("backup", "--source", source, "--target", PathInRoot("absent/new.db")));
        Assert.Equal(1, Run("backup", "--source", source, "--target", _root));
        Assert.False(Directory.Exists(PathInRoot("absent")));
    }

    [Theory]
    [InlineData()]
    [InlineData("--unknown")]
    [InlineData("backup", "--source")]
    [InlineData("backup", "--source", "a", "--source", "b", "--target", "c")]
    [InlineData("verify", "--source", "a", "--target", "b")]
    [InlineData("backup", "--source", "a", "--target", "b", "--overwrite")]
    public void InvalidArgumentsReturnFailure(params string[] arguments) => Assert.Equal(1, Run(arguments));

    [Fact]
    public void HelpIsAvailableWithoutTouchingDatabase() => Assert.Equal(0, Run("--help"));

    [Fact]
    public void WindowsAlternateStreamsAndDevicePathsAreRejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = CreateDatabase();
        Assert.Equal(1, Run("backup", "--source", source, "--target", PathInRoot("new.db:stream")));
        Assert.Equal(1, Run("backup", "--source", source, "--target", @"\\?\" + PathInRoot("new.db")));
        Assert.Equal(1, Run("verify", "--source", @"\\.\" + source));
        Assert.False(File.Exists(PathInRoot("new.db")));
    }

    private string CreateDatabase()
    {
        var path = PathInRoot("source.db");
        using var db = Open(path);
        Execute(db, "CREATE TABLE Saves (Id INTEGER PRIMARY KEY, Gold INTEGER NOT NULL); INSERT INTO Saves VALUES (1, 123);");
        return path;
    }

    private string PathInRoot(string name) => Path.Combine(_root, name);
    private static int Run(params string[] args) => MaintenanceApplication.Run(args, new StringWriter(), new StringWriter());
    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        return db;
    }
    private static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private static long ReadGold(string path)
    {
        using var db = Open(path);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT Gold FROM Saves WHERE Id=1;";
        return (long)command.ExecuteScalar()!;
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(_root);
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("IdleGameMaintenance-", StringComparison.Ordinal))
            throw new InvalidOperationException("Test cleanup target is outside its temporary workspace.");
        Directory.Delete(full, recursive: true);
    }
}
