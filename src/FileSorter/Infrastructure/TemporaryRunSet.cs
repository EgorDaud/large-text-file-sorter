using Shared;

namespace FileSorter.Infrastructure;

internal sealed class TemporaryRunSet : IDisposable
{
    private const int MaxPrivateDirectoryAttempts = 8;

    private readonly string _directory;

    // Spillers create paths concurrently; guards _paths, _deferred and _nextRun.
    private readonly Lock _gate = new();
    private readonly HashSet<string> _paths = [];
    private readonly HashSet<string> _deferred = [];

    private int _nextRun;

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

    // Best-effort: an AV scanner or indexer can briefly hold a run, which must not abort the sort.
    // Failures stay tracked and are retried here and in Dispose, since the capacity check assumes freed runs.
    public void Delete(string runPath)
    {
        if (TryDelete(runPath) is { } failure)
        {
            Console.Error.WriteLine(
                $"Could not delete the temporary run file at {runPath}, retrying later: {failure.Message}");
        }

        string[] deferred;
        lock (_gate)
        {
            deferred = [.. _deferred];
        }

        foreach (string path in deferred)
        {
            if (path != runPath)
            {
                TryDelete(path);
            }
        }
    }

    private Exception? TryDelete(string runPath)
    {
        try
        {
            File.Delete(runPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                _deferred.Add(runPath);
            }

            return ex;
        }

        lock (_gate)
        {
            _paths.Remove(runPath);
            _deferred.Remove(runPath);
        }

        return null;
    }

    public void Dispose()
    {
        string[] paths;
        string? privateDirectory;
        lock (_gate)
        {
            paths = [.. _paths];
            _paths.Clear();
            _deferred.Clear();
            privateDirectory = _privateDirectory;
        }

        int leftover = 0;
        foreach (string path in paths)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                leftover++;
            }
        }

        if (privateDirectory is not null)
        {
            try
            {
                Directory.Delete(privateDirectory);
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                string runs = leftover > 0 ? $", which still holds {leftover} run file(s)" : string.Empty;
                Console.Error.WriteLine($"Could not remove the temporary directory {privateDirectory}{runs}: {ex.Message}");
            }
        }
    }

    private string CreatePrivateDirectory()
    {
        for (int attempt = 0; attempt < MaxPrivateDirectoryAttempts; attempt++)
        {
            string candidate = Path.Combine(_directory, $"sorter-{Environment.ProcessId}-{StagingFile.RandomSuffix()}");
            if (Directory.Exists(candidate))
            {
                continue;
            }

            Directory.CreateDirectory(candidate);
            return candidate;
        }

        throw new IOException($"Could not create a private temporary directory beneath '{_directory}' after {MaxPrivateDirectoryAttempts} attempts.");
    }
}
