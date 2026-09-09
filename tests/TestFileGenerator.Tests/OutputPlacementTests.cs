using TestFileGenerator.Generation;
using Xunit;

namespace TestFileGenerator.Tests;

// Program.WriteToOutput, not Main: Main is a private entry point and cannot be driven
// directly from a test, the way FileSorter's own Program.RunAsync can be. WriteToOutput
// carries the whole write-then-replace behaviour Main delegates to, so exercising it
// here is exercising the real path.
public sealed class OutputPlacementTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "GeneratorTests", Guid.NewGuid().ToString("N"));

    public OutputPlacementTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Case", "GW-01")]
    public void A_successful_write_replaces_an_existing_destination_and_leaves_no_staging_file()
    {
        string outputPath = Path.Combine(_directory, "out.txt");
        File.WriteAllText(outputPath, "stale content from an earlier run\n");

        GeneratorOptions options = new(outputPath, TargetBytes: 256, Seed: 1, DuplicateRatio: 0.1);
        LineComposer composer = new(options);

        long written = Program.WriteToOutput(options, composer, static _ => { });

        Assert.True(written > 0);
        Assert.Equal(written, new FileInfo(outputPath).Length);
        Assert.Empty(Directory.GetFiles(_directory, "*.partial"));
    }

    [Fact]
    [Trait("Case", "GW-02")]
    public void A_failed_replace_of_a_locked_destination_leaves_it_untouched_and_deletes_only_the_staging_file()
    {
        // Windows-only reproduction: a destination held open for read with delete
        // sharing denies the write access the final File.Move needs, without denying
        // the delete a plain rename would use. A Unix rename does not distinguish
        // these cases, so nothing here would fail on Linux the same way.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FileShare-based write denial is a Windows sharing-mode concept.");

        string outputPath = Path.Combine(_directory, "out.txt");
        byte[] previousContent = "previous output, not this run's"u8.ToArray();
        File.WriteAllBytes(outputPath, previousContent);

        GeneratorOptions options = new(outputPath, TargetBytes: 256, Seed: 1, DuplicateRatio: 0.1);
        LineComposer composer = new(options);

        using (new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            Assert.Throws<UnauthorizedAccessException>(
                () => Program.WriteToOutput(options, composer, static _ => { }));
        }

        Assert.Equal(previousContent, File.ReadAllBytes(outputPath));
        Assert.Empty(Directory.GetFiles(_directory, "*.partial"));
    }
}
