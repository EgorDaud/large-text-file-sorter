using System.Security.Cryptography;

namespace FileSorter.Startup;

internal sealed class TemporaryRunSet : IDisposable
{
    private const int MaxPrivateDirectoryAttempts = 8;

    private readonly string _directory;

    // Spillers create paths concurrently. The lock protects both the cleanup set and
    // the monotonically increasing run number.
    private readonly Lock _gate = new();
    private readonly HashSet<string> _paths = [];

    private int _nextRun;

    // Created only after Program's disk-capacity check has passed.
    private string? _privateDirectory;

    public TemporaryRunSet(string tempDirectory)
    {
        _directory = tempDirectory;
        Directory.CreateDirectory(_directory);
    }

    public string CreateRunPath()
    {
        lock (_gate)
        {
            _privateDirectory ??= CreatePrivateDirectory();
            string path = Path.Combine(_privateDirectory, $"run-{++_nextRun:D8}.tmp");
            _paths.Add(path);
            return path;
        }
    }

    // Best-effort: a scanner or indexer can briefly hold a finished run, and that must
    // not abort a long merge or make Program discard a completed output. A path is
    // untracked only once its delete succeeded, so a failed one stays tracked and
    // Dispose retries it. The delete itself stays outside the lock.
    public void Delete(string runPath)
    {
        try
        {
            File.Delete(runPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"Could not delete the temporary run file at {runPath}, retrying at exit: {ex.Message}");
            return;
        }

        lock (_gate)
        {
            _paths.Remove(runPath);
        }
    }

    public void Dispose()
    {
        // Delete outside the lock because filesystem operations can block.
        string[] paths;
        string? privateDirectory;
        lock (_gate)
        {
            paths = [.. _paths];
            _paths.Clear();
            privateDirectory = _privateDirectory;
        }

        // Cleanup can follow a partial failure, so an already-removed file is routine.
        foreach (string path in paths)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // The private directory belongs to this invocation.
        if (privateDirectory is not null)
        {
            try
            {
                Directory.Delete(privateDirectory);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // A process ID and random suffix isolate concurrent invocations. A pre-existing
    // candidate is treated as a collision and retried.
    private string CreatePrivateDirectory()
    {
        for (int attempt = 0; attempt < MaxPrivateDirectoryAttempts; attempt++)
        {
            string candidate = Path.Combine(_directory, $"sorter-{Environment.ProcessId}-{RandomSuffix()}");
            if (Directory.Exists(candidate))
            {
                continue;
            }

            Directory.CreateDirectory(candidate);
            return candidate;
        }

        throw new IOException($"Could not create a private temporary directory beneath '{_directory}' after {MaxPrivateDirectoryAttempts} attempts.");
    }

    // Shared with StagingFile so both name their files the same way.
    internal static string RandomSuffix()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
