using FileSorter.Planning;
using Shared;

namespace FileSorter.Cli;

internal static class VolumeCapacity
{
    private static readonly StringComparison VolumeRootComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static void Check(long inputSizeBytes, string tempDirectory, string outputDirectory)
    {
        long? tempFreeBytes = ProbeFreeSpace(tempDirectory);
        long? outputFreeBytes = ProbeFreeSpace(outputDirectory);
        string? tempRoot = ResolveVolumeRoot(tempDirectory);
        string? outputRoot = ResolveVolumeRoot(outputDirectory);
        bool sameVolume = tempRoot is not null && outputRoot is not null
            && string.Equals(tempRoot, outputRoot, VolumeRootComparison);

        if (sameVolume)
        {
            Report("temp and output", tempDirectory, TempCapacity.EvaluateSameVolume(inputSizeBytes, tempFreeBytes));
            return;
        }

        Report("temp", tempDirectory, TempCapacity.Evaluate(inputSizeBytes, tempFreeBytes));
        Report("output", outputDirectory, TempCapacity.EvaluateOutputVolume(inputSizeBytes, outputFreeBytes));
    }

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
        // Windows DriveInfo throws ArgumentException for a UNC root; treat it as unknown.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

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

    private static void Report(string volumeLabel, string directory, CapacityDecision decision)
    {
        if (decision.Outcome == CapacityOutcome.Insufficient)
        {
            throw new PreflightException(
                $"Insufficient space on the {volumeLabel} volume ('{directory}'): need {ByteSize.Describe(decision.RequiredBytes)}, " +
                $"but only {ByteSize.Describe(decision.AvailableBytes)} is available.",
                ExitCodes.InsufficientTempSpace);
        }

        if (decision.Outcome == CapacityOutcome.Unknown)
        {
            Console.Error.WriteLine(
                $"Could not determine free space for the {volumeLabel} volume ('{directory}'); proceeding " +
                $"without a capacity check there (need approximately {ByteSize.Describe(decision.RequiredBytes)}).");
        }
    }
}
