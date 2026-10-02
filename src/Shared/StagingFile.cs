using System.Security.Cryptography;

namespace Shared;

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

    internal static string RandomSuffix()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
