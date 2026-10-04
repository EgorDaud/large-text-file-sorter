using FileSorter.Infrastructure;

namespace FileSorter.Merging;

internal static class OutputFile
{
    // Unlink, don't truncate: truncating through a hard link or symlink could destroy the input.
    public static FileStream CreateFresh(string path, FileShare share, FileOptions options)
    {
        File.Delete(path);
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, share, FileStreams.Unbuffered, options);
    }
}
