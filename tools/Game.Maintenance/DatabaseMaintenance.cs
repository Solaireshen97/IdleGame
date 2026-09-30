using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Game.Maintenance;

public static class DatabaseMaintenance
{
    private static readonly string[] Sidecars = ["-wal", "-shm", "-journal"];
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static MaintenanceReport Verify(string source)
    {
        source = ExistingDatabase(source);
        using var connection = Open(source, SqliteOpenMode.ReadOnly);
        CheckIntegrity(connection);
        // A live WAL database's main-file hash is not the hash of the logical snapshot.
        return new("verify", source, null, null, null, DateTime.UtcNow);
    }

    public static MaintenanceReport Copy(string source, string target, bool restore)
    {
        source = ExistingDatabase(source);
        target = FullPath(target);
        if (string.Equals(source, target, PathComparison))
            throw new IOException("Source and target must be different paths.");
        var parent = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Target parent directory must already exist.");
        RejectExistingTarget(target);
        if (restore && Sidecars.Any(suffix => File.Exists(source + suffix) || Directory.Exists(source + suffix)))
            throw new IOException("Restore requires a standalone backup without WAL, SHM, or journal sidecars. Use backup first.");

        // Only this tool's newly created temporary files are eligible for cleanup.
        // Publish by a non-overwriting rename after checking the entire snapshot.
        var temporary = Path.Combine(parent, $".maintenance-{Guid.NewGuid():N}.db");
        var ownsTemporary = false;
        try
        {
            using var input = Open(source, SqliteOpenMode.ReadOnly);
            using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            ownsTemporary = true;
            using (var destination = Open(temporary, SqliteOpenMode.ReadWrite))
            {
                input.BackupDatabase(destination);
                using var journal = destination.CreateCommand();
                journal.CommandText = "PRAGMA journal_mode=DELETE;";
                if (!string.Equals(Convert.ToString(journal.ExecuteScalar()), "delete", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Could not make the backup a standalone database.");
                CheckIntegrity(destination);
            }

            if (Sidecars.Any(suffix => File.Exists(temporary + suffix)))
                throw new IOException("Backup still has SQLite sidecars; it was not published.");
            string hash;
            long bytes;
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Flush(flushToDisk: true);
                bytes = stream.Length;
                hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
            FullPath(target); // Recheck ancestors before publication.
            RejectExistingTarget(target);
            File.Move(temporary, target, overwrite: false);
            ownsTemporary = false;
            return new(restore ? "restore" : "backup", source, target, bytes, hash, DateTime.UtcNow);
        }
        finally
        {
            if (ownsTemporary)
            {
                foreach (var ownedFile in Sidecars.Select(suffix => temporary + suffix).Prepend(temporary))
                {
                    try
                    {
                        FullPath(ownedFile);
                        File.Delete(ownedFile);
                    }
                    catch (IOException) { /* Leave an unpublished temporary file if another process holds it. */ }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static string ExistingDatabase(string path)
    {
        path = FullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Source database does not exist.", path);
        foreach (var suffix in Sidecars) FullPath(path + suffix);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> header = stackalloc byte[16];
        if (stream.Length < 100 || stream.Read(header) != header.Length || !header.SequenceEqual("SQLite format 3\0"u8))
            throw new IOException("Source is empty or does not have a valid SQLite database header.");
        return path;
    }

    private static string FullPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Use fully qualified absolute database paths.");
        if (OperatingSystem.IsWindows() && (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            path[Path.GetPathRoot(path)!.Length..].Contains(':')))
            throw new ArgumentException("Windows device paths and alternate data streams are not supported.");
        var full = Path.GetFullPath(path);
        // Reject existing links anywhere in the chain, including the final file.
        for (string? item = full; item is not null; item = Path.GetDirectoryName(item))
        {
            try
            {
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Paths through symbolic links or junctions are not supported.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }

    private static void RejectExistingTarget(string target)
    {
        foreach (var path in Sidecars.Select(suffix => target + suffix).Prepend(target))
        {
            FullPath(path);
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException("Target or its SQLite sidecar already exists. Choose a new database path.");
        }
    }

    private static void CheckIntegrity(SqliteConnection connection)
    {
        using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        using (var reader = integrity.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != "ok" || reader.Read())
                throw new InvalidOperationException("SQLite integrity_check failed; the database was not published.");
        }
        using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_key_check;";
        using var violations = foreignKeys.ExecuteReader();
        if (violations.Read())
            throw new InvalidOperationException("SQLite foreign_key_check failed; the database was not published.");
    }
}

public sealed record MaintenanceReport(string Operation, string Source, string? Target,
    long? Bytes, string? Sha256, DateTime CheckedAtUtc);
