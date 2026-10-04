using Shared;
using TestFileGenerator.Generation;
using Xunit;

namespace TestFileGenerator.Tests;

// Run writes to the process-wide Console.Error, which each test here swaps out.
[Collection("Program")]
public sealed class GenerateCommandTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "GeneratorTests", Guid.NewGuid().ToString("N"));

    public GenerateCommandTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    [Trait("Case", "GC-01")]
    public void A_successful_run_exits_0_and_reports_the_size_written()
    {
        string outputPath = Path.Combine(_directory, "out.txt");
        GeneratorOptions options = new(outputPath, TargetBytes: 256, Seed: 1, DuplicateRatio: 0.1);

        (int exitCode, string stderr) = RunCaptured(options, CancellationToken.None);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.StartsWith("Wrote ", stderr);
        Assert.Contains($" to {outputPath} in ", stderr);
        Assert.True(File.Exists(outputPath));
    }

    [Fact]
    [Trait("Case", "GC-02")]
    public void A_write_failure_exits_5_naming_the_output_and_creates_nothing()
    {
        string outputPath = Path.Combine(_directory, "missing-directory", "out.txt");
        GeneratorOptions options = new(outputPath, TargetBytes: 256, Seed: 1, DuplicateRatio: 0.1);

        (int exitCode, string stderr) = RunCaptured(options, CancellationToken.None);

        Assert.Equal(ExitCodes.IoFailure, exitCode);
        Assert.StartsWith($"Output file '{outputPath}' could not be written: ", stderr);
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    [Trait("Case", "GC-03")]
    public void A_cancelled_run_exits_130_silently_and_leaves_no_staging_file()
    {
        GeneratorOptions options = new(Path.Combine(_directory, "out.txt"), TargetBytes: 4 << 20, Seed: 1, DuplicateRatio: 0.1);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        (int exitCode, string stderr) = RunCaptured(options, cts.Token);

        Assert.Equal(ExitCodes.Cancelled, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    private static (int ExitCode, string Stderr) RunCaptured(GeneratorOptions options, CancellationToken ct)
    {
        TextWriter original = Console.Error;
        StringWriter captured = new();
        Console.SetError(captured);
        try
        {
            return (GenerateCommand.Run(options, ct), captured.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }
}
