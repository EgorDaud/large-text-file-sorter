using Shared;

namespace TestFileGenerator.Generation;

internal static class StagedOutput
{
    private const int OutputBufferBytes = 1 << 20;

    public static long Write(
        GeneratorOptions options, LineComposer composer, Action<long> reportProgress, CancellationToken ct)
    {
        string stagingPath = StagingFile.CreatePath(options.OutputPath);
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
            StagingFile.Delete(stagingPath);
            throw;
        }
    }
}
