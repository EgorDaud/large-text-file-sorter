using System.Security.Cryptography;

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
            string candidate = $"{destinationPath}.{RandomSuffix()}.partial";
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Could not choose a staging file name beside '{destinationPath}' after {MaxAttempts} attempts.");
    }

    // Removes only this staging file. Missing files are harmless; other cleanup failures
    // remain visible to the caller.
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
            Console.Error.WriteLine($"Could not remove the staging file at {stagingPath}: {ex.Message}");
        }
    }

    private static string RandomSuffix()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
