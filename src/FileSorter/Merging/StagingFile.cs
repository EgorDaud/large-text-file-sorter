using FileSorter.Startup;

namespace FileSorter.Merging;

// Creates a unique staging path beside the destination for write-then-replace operations.
// The random suffix avoids collisions with user files and temporary run names.
internal static class StagingFile
{
    private const int MaxAttempts = 8;

    public static string CreatePath(string destinationPath)
    {
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            string candidate = $"{destinationPath}.{TemporaryRunSet.RandomSuffix()}.partial";
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Could not choose a staging file name beside '{destinationPath}' after {MaxAttempts} attempts.");
    }

    // Removes only this partial file (a staging file or an incomplete merge output). Missing
    // files are harmless; cleanup failures are reported without replacing the original error.
    public static void Delete(string stagingPath)
    {
        try
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not remove the partial file at {stagingPath}: {ex.Message}");
        }
    }
}
