using FileSorter.Startup;

namespace FileSorter.Merging;

internal static class RunPlacement
{
    // If a move cannot cross volumes, copy to a staging file beside outputPath, then move
    // it into place. A failure leaves outputPath unchanged or fully replaced. Delete the
    // source last and best-effort, so cleanup failure cannot fail a completed placement.
    public static void Place(string runPath, string outputPath, Func<string, string, bool> tryMove, TemporaryRunSet runs)
    {
        if (tryMove(runPath, outputPath))
        {
            return;
        }

        string staging = StagingFile.CreatePath(outputPath);
        try
        {
            File.Copy(runPath, staging, overwrite: false);
            File.Move(staging, outputPath, overwrite: true);
        }
        catch
        {
            StagingFile.Delete(staging);
            throw;
        }

        runs.Delete(runPath);
    }
}
