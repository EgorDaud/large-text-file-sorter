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

    public void Delete(string runPath)
    {
        lock (_gate)
        {
            _paths.Remove(runPath);
        }

        File.Delete(runPath);
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

    private static string RandomSuffix()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
