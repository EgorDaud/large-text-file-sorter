using FileSorter.Infrastructure;
using Shared;

namespace FileSorter.Merging;

internal static class RunPlacement
{
    public static void Place(string runPath, string outputPath, TemporaryRunSet runs, Func<string, string, bool>? tryMove = null)
    {
        string staging = StagingFile.CreatePath(outputPath);
        bool copied = false;
        try
        {
            if (!(tryMove ?? TryMove)(runPath, staging))
            {
                File.Copy(runPath, staging, overwrite: false);
                copied = true;
            }

            ReplaceDestination(staging, outputPath);
        }
        catch
        {
            StagingFile.Delete(staging);
            throw;
        }

        if (copied)
        {
            runs.Delete(runPath);
        }
    }

    public static void PlaceEmpty(string outputPath)
    {
        string staging = StagingFile.CreatePath(outputPath);
        try
        {
            using (new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            ReplaceDestination(staging, outputPath);
        }
        catch
        {
            StagingFile.Delete(staging);
            throw;
        }
    }

    // Only this rename's failure is the destination's fault; staging-write failures stay plain I/O.
    private static void ReplaceDestination(string staging, string outputPath)
    {
        try
        {
            File.Move(staging, outputPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DestinationReplaceFailedException(outputPath, ex);
        }
    }

    // A cross-volume move can throw after copying, leaving a file at the staging path.
    private static bool TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to);
            return true;
        }
        catch (IOException)
        {
            StagingFile.Delete(to);
            return false;
        }
    }
}
