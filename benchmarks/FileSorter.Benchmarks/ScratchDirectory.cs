namespace FileSorter.Benchmarks;

// A unique scratch directory under the system temp folder for one benchmark run.
// Removal is best effort: a file a scanner still holds open must not fail the run.
internal static class ScratchDirectory
{
    public static string Create(string name)
    {
        string path = Path.Combine(Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort; see the type comment.
        }
    }
}
