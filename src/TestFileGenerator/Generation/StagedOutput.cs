using Shared;

namespace TestFileGenerator.Generation;

internal static class StagedOutput
{
    private const int OutputBufferBytes = 1 << 20;

    public static long Write(GeneratorOptions options, Action<long> reportProgress, CancellationToken ct)
    {
        // Built before the staging file exists, so an invalid option leaves nothing behind.
        FileWriter writer = new(new LineComposer(options), options.TargetBytes);
        string stagingPath = StagingFile.CreatePath(options.OutputPath);
        try
        {
            long written;
            using (FileStream output = new(
                stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                OutputBufferBytes, FileOptions.SequentialScan))
            {
                written = writer.Write(output, ct, reportProgress);
            }

            File.Move(stagingPath, options.OutputPath, overwrite: true);
            return written;
        }
        catch
        {
            StagingFile.Delete(stagingPath);
            throw;
        }
    }
}
