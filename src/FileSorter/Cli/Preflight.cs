using FileSorter.Infrastructure;

namespace FileSorter.Cli;

// Checks the output and temp directories before any input is read or run file is written.
// Each failure throws a PreflightException carrying exit 3.
internal static class Preflight
{
    // Return "." for a bare filename because an empty path is not a usable directory.
    internal static string DirectoryOf(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(directory) ? "." : directory;
    }

    // Probe a sibling file so validation never truncates an existing output.
    internal static void ValidateOutputDirectory(string directory)
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

    // Map directory-creation failures to argument errors. An existing but unwritable
    // temp directory still fails only when the first run file is created.
    internal static TemporaryRunSet CreateTemporaryRunSet(string tempDirectory)
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
