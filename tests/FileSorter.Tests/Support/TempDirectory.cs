namespace FileSorter.Tests.Support;

/// <summary>
/// A unique, already-created scratch directory that removes itself on disposal. Cleanup
/// failures are swallowed: a scratch directory a scanner or the OS still holds open must
/// not turn a passing test red.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FileSorterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort; see the type comment.
        }
    }
}
