using FileSorter.Infrastructure;
using Shared;

namespace FileSorter.Cli;

internal static class Preflight
{
    public static FileInfo ValidateReadableFile(string path, string role)
    {
        FileInfo info = new(path);
        if (!info.Exists)
        {
            throw new PreflightException($"{role} file not found: '{path}'.", ExitCodes.InvalidArguments);
        }

        try
        {
            using FileStream probe = File.OpenRead(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PreflightException($"{role} file '{path}' cannot be read: {ex.Message}", ExitCodes.InvalidArguments);
        }

        return info;
    }

    public static string DirectoryOf(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(directory) ? "." : directory;
    }

    // Probe a sibling, never the output itself, so validation cannot truncate an existing output.
    public static void ValidateOutputDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new PreflightException($"Output directory '{directory}' does not exist.", ExitCodes.InvalidArguments);
        }

        string probePath = Path.Combine(directory, $".sorter-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using (File.Create(probePath))
            {
            }

            File.Delete(probePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new PreflightException(
                $"Output directory '{directory}' is not writable: {ex.Message}", ExitCodes.InvalidArguments);
        }
    }

    public static TemporaryRunSet CreateTemporaryRunSet(string tempDirectory)
    {
        try
        {
            return new TemporaryRunSet(tempDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new PreflightException(
                $"--temp directory '{tempDirectory}' could not be created or used: {ex.Message}", ExitCodes.InvalidArguments);
        }
    }
}
