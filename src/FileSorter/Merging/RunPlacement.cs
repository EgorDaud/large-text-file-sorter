namespace FileSorter.Merging;

internal static class RunPlacement
{
    // If a move cannot cross volumes, copy to a staging file beside outputPath, then move
    // it into place. A failure leaves outputPath unchanged or fully replaced. Delete the
    // source last, so cleanup failure cannot leave a partial destination.
    public static void Place(string runPath, string outputPath, Func<string, string, bool> tryMove)
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

        File.Delete(runPath);
    }
}
