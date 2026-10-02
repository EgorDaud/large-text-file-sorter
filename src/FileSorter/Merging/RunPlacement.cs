using FileSorter.Startup;

namespace FileSorter.Merging;

internal static class RunPlacement
{
    // If a move cannot cross volumes, copy to a staging file beside outputPath, then move
    // it into place. A failure leaves outputPath unchanged or fully replaced. Delete the
    // source last and best-effort, so cleanup failure cannot fail a completed placement.
    // Tests pass their own tryMove to force the copy fallback.
    public static void Place(string runPath, string outputPath, TemporaryRunSet runs, Func<string, string, bool>? tryMove = null)
    {
        if ((tryMove ?? TryMove)(runPath, outputPath))
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

    // Empty input still needs an output file, created the same atomic way.
    public static void PlaceEmpty(string outputPath)
    {
        string staging = StagingFile.CreatePath(outputPath);
        try
        {
            using (new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            File.Move(staging, outputPath, overwrite: true);
        }
        catch
        {
            StagingFile.Delete(staging);
            throw;
        }
    }

    private static bool TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
