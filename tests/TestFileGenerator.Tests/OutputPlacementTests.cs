using TestFileGenerator.Generation;
using Xunit;

namespace TestFileGenerator.Tests;

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

        long written = StagedOutput.Write(options, static _ => { }, TestContext.Current.CancellationToken);

        Assert.True(written > 0);
        Assert.Equal(written, new FileInfo(outputPath).Length);
        Assert.Empty(Directory.GetFiles(_directory, "*.partial"));
    }

    [Fact]
    [Trait("Case", "GW-02")]
    public void A_failed_replace_of_a_locked_destination_leaves_it_untouched_and_deletes_only_the_staging_file()
    {
        // On Windows a reader sharing only Read | Delete denies the write access File.Move's overwrite needs.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FileShare-based write denial is a Windows sharing-mode concept.");

        string outputPath = Path.Combine(_directory, "out.txt");
        byte[] previousContent = "previous output, not this run's"u8.ToArray();
        File.WriteAllBytes(outputPath, previousContent);

        GeneratorOptions options = new(outputPath, TargetBytes: 256, Seed: 1, DuplicateRatio: 0.1);

        using (new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            Assert.Throws<UnauthorizedAccessException>(
                () => StagedOutput.Write(options, static _ => { }, TestContext.Current.CancellationToken));
        }

        Assert.Equal(previousContent, File.ReadAllBytes(outputPath));
        Assert.Empty(Directory.GetFiles(_directory, "*.partial"));
    }

    [Fact]
    [Trait("Case", "GW-03")]
    public void A_cancelled_write_deletes_the_staging_file_and_leaves_an_existing_destination_untouched()
    {
        // Deterministic because FileWriter polls the token right after each progress report.
        string outputPath = Path.Combine(_directory, "out.txt");
        byte[] previousContent = "previous output, not this run's"u8.ToArray();
        File.WriteAllBytes(outputPath, previousContent);

        GeneratorOptions options = new(outputPath, TargetBytes: 4 << 20, Seed: 1, DuplicateRatio: 0.1);
        using CancellationTokenSource cts = new();

        Assert.ThrowsAny<OperationCanceledException>(
            () => StagedOutput.Write(options, _ => cts.Cancel(), cts.Token));

        Assert.Equal(previousContent, File.ReadAllBytes(outputPath));
        Assert.Empty(Directory.GetFiles(_directory, "*.partial"));
    }
}
