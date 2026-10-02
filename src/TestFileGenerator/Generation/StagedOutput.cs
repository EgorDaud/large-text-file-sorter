using System.Security.Cryptography;

namespace TestFileGenerator.Generation;

// Writes the generated file beside the destination and moves it into place only once it
// is complete, so an interrupted run never leaves a short file under the output name.
internal static class StagedOutput
{
    private const int OutputBufferBytes = 1 << 20;
    private const int MaxStagingAttempts = 8;

    // Write beside the output and replace it only after a successful write.
    internal static long Write(
        GeneratorOptions options, LineComposer composer, Action<long> reportProgress, CancellationToken ct)
    {
        string stagingPath = CreateStagingPath(options.OutputPath);
        try
        {
            long written;
            using (FileStream output = new(
                stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                OutputBufferBytes, FileOptions.SequentialScan))
            {
                written = FileWriter.Write(output, composer, options.TargetBytes, ct, reportProgress);
            }

            File.Move(stagingPath, options.OutputPath, overwrite: true);
            return written;
        }
        catch
        {
            // A staging file can look like a smaller valid output, so remove it.
            DeleteStagingFile(stagingPath);
            throw;
        }
    }

    // This duplicates Merging/StagingFile because the two executables share no project.
    // Keep its retry and cleanup behavior aligned with the sorter.
    private static string CreateStagingPath(string outputPath)
    {
        for (int attempt = 0; attempt < MaxStagingAttempts; attempt++)
        {
            string candidate = $"{outputPath}.{RandomStagingSuffix()}.partial";
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException(
            $"Could not choose a staging file name beside '{outputPath}' after {MaxStagingAttempts} attempts.");
    }

    private static string RandomStagingSuffix()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // Cleanup failures must not hide the original write failure.
    private static void DeleteStagingFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception removal) when (removal is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not remove the staging file at {path}: {removal.Message}");
        }
    }
}
