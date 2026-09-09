using System.Diagnostics.CodeAnalysis;

namespace FileSorter.Startup;

// Preflight validation for output and temp directories, volume capacity, and diagnostics.
internal static class CapacityProbe
{
    // Return "." for a bare filename because an empty path is not a usable directory.
    internal static string DirectoryOf(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(directory) ? "." : directory;
    }

    // Probe a sibling file so validation never truncates an existing output.
    internal static bool TryValidateOutputDirectory(string directory, [NotNullWhen(false)] out string? error)
    {
        if (!Directory.Exists(directory))
        {
            error = $"Output directory '{directory}' does not exist.";
            return false;
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
            error = $"Output directory '{directory}' is not writable: {ex.Message}";
            return false;
        }

        error = null;
        return true;
    }

    // Map directory-creation failures to argument errors. An existing but unwritable
    // temp directory still fails only when the first run file is created.
    internal static bool TryCreateTemporaryRunSet(
        string tempDirectory,
        [NotNullWhen(true)] out TemporaryRunSet? runs,
        [NotNullWhen(false)] out string? error)
    {
        try
        {
            runs = new TemporaryRunSet(tempDirectory);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            runs = null;
            error = $"--temp directory '{tempDirectory}' could not be created or used: {ex.Message}";
            return false;
        }
    }

    // Windows volume roots are case-insensitive.
    private static readonly StringComparison VolumeRootComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static long? ProbeFreeSpace(string directory)
    {
        string? root = ResolveVolumeRoot(directory);
        if (root is null)
        {
            return null;
        }

        try
        {
            DriveInfo drive = new(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Shared resolution for DriveInfo and same-volume comparisons.
    private static string? ResolveVolumeRoot(string directory)
    {
        try
        {
            string full = Path.GetFullPath(directory);

            // Unix DriveInfo resolves the containing filesystem from an absolute path.
            return OperatingSystem.IsWindows()
                ? (Path.GetPathRoot(full) is { Length: > 0 } value ? value : directory)
                : full;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // Insufficient capacity exits with 2; unreported capacity warns and proceeds.
    private static int? ReportCapacityIssue(string volumeLabel, string directory, CapacityDecision decision)
    {
        if (decision.Outcome == CapacityOutcome.Insufficient)
        {
            Console.Error.WriteLine(
                $"Insufficient space on the {volumeLabel} volume ('{directory}'): need {ProgressReporter.Describe(decision.RequiredBytes)}, " +
                $"but only {ProgressReporter.Describe(decision.AvailableBytes)} is available.");
            return ExitCodes.InsufficientTempSpace;
        }

        if (decision.Outcome == CapacityOutcome.Unknown)
        {
            // Unreported free space is unknown, not zero.
            Console.Error.WriteLine(
                $"Could not determine free space for the {volumeLabel} volume ('{directory}'); proceeding " +
                $"without a capacity check there (need approximately {ProgressReporter.Describe(decision.RequiredBytes)}).");
        }

        return null;
    }

    // Same-volume capacity covers runs and output together. Different volumes are
    // checked separately because the final output can exhaust its own volume.
    internal static int? CheckCapacity(long inputSizeBytes, string tempDirectory, string outputDirectory)
    {
        long? tempFreeBytes = ProbeFreeSpace(tempDirectory);
        long? outputFreeBytes = ProbeFreeSpace(outputDirectory);
        string? tempRoot = ResolveVolumeRoot(tempDirectory);
        string? outputRoot = ResolveVolumeRoot(outputDirectory);
        bool sameVolume = tempRoot is not null && outputRoot is not null
            && string.Equals(tempRoot, outputRoot, VolumeRootComparison);

        if (sameVolume)
        {
            CapacityDecision combined = TempCapacity.EvaluateSameVolume(inputSizeBytes, tempFreeBytes);
            return ReportCapacityIssue("temp and output", tempDirectory, combined);
        }

        CapacityDecision tempCapacity = TempCapacity.Evaluate(inputSizeBytes, tempFreeBytes);
        if (ReportCapacityIssue("temp", tempDirectory, tempCapacity) is int tempExitCode)
        {
            return tempExitCode;
        }

        CapacityDecision outputCapacity = TempCapacity.EvaluateOutputVolume(inputSizeBytes, outputFreeBytes);
        return ReportCapacityIssue("output", outputDirectory, outputCapacity);
    }
}
