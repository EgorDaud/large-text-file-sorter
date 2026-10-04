using FileSorter.Cli;
using FileSorter.LineFormat;
using FileSorter.Planning;
using FileSorter.Tests.Support;
using Shared;
using Xunit;
using static FileSorter.Tests.EndToEnd.SortHarness;
using static FileSorter.Tests.Support.TestTimeouts;

namespace FileSorter.Tests.EndToEnd;

[Collection("Program")]
public sealed class SortFailureTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task Sorting_with_a_missing_output_directory_fails_fast_before_phase_one_starts()
    {
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "no-such-directory", "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        File.WriteAllText(inputPath, "1. Apple\n2. Banana\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        (int exitCode, string stderr) = await RunThroughConsoleRunCapturedAsync(options);

        Assert.Equal(3, exitCode);
        Assert.Contains("no-such-directory", stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(tempDirectory));
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task Sorting_with_an_unusable_temp_directory_fails_fast_before_phase_one_starts()
    {
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp-is-actually-a-file");
        File.WriteAllText(inputPath, "1. Apple\n2. Banana\n");
        File.WriteAllText(tempDirectory, "not a directory");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        (int exitCode, string stderr) = await RunThroughConsoleRunCapturedAsync(options);

        Assert.Equal(3, exitCode);
        Assert.Contains(tempDirectory, stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task Sorting_a_malformed_line_surfaces_MalformedLineException_naming_the_offending_line()
    {
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");

        File.WriteAllText(inputPath, "1. Apple\nno separator here\n3. Banana\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);

        MalformedLineException thrown = await Assert.ThrowsAsync<MalformedLineException>(
            () => SortCommand.RunAsync(options, TestContext.Current.CancellationToken)
                .WaitAsync(BoundedWait, TestContext.Current.CancellationToken));

        Assert.Equal(2, thrown.LineNumber);
        Assert.Contains("no separator here", thrown.Message);
    }

    [Fact]
    [Trait("Case", "ET-11")]
    public async Task An_empty_input_sort_reports_a_read_only_existing_destination_by_name_and_exits_3()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "A read-only attribute blocks File.Move's replace only on Windows.");

        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        File.WriteAllBytes(inputPath, []);
        byte[] previousContent = "previous output, not this run's"u8.ToArray();
        File.WriteAllBytes(outputPath, previousContent);
        File.SetAttributes(outputPath, FileAttributes.ReadOnly);

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        try
        {
            (int exitCode, string stderr) = await RunThroughConsoleRunCapturedAsync(options);

            Assert.Equal(3, exitCode);
            Assert.Contains(outputPath, stderr, StringComparison.Ordinal);
            Assert.Equal(previousContent, File.ReadAllBytes(outputPath));
            Assert.Empty(Directory.GetFiles(_directory.Path, "*.partial"));
        }
        finally
        {
            File.SetAttributes(outputPath, FileAttributes.Normal);
        }
    }

    [Fact]
    [Trait("Case", "ET-12")]
    public async Task A_single_run_sort_reports_a_read_only_existing_destination_by_name_and_exits_3()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "A read-only attribute blocks File.Move's replace only on Windows.");

        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        File.WriteAllText(inputPath, "1. Apple\n");
        byte[] previousContent = "previous output, not this run's"u8.ToArray();
        File.WriteAllBytes(outputPath, previousContent);
        File.SetAttributes(outputPath, FileAttributes.ReadOnly);

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        try
        {
            (int exitCode, string stderr) = await RunThroughConsoleRunCapturedAsync(options);

            Assert.Equal(3, exitCode);
            Assert.Contains(outputPath, stderr, StringComparison.Ordinal);
            Assert.Equal(previousContent, File.ReadAllBytes(outputPath));
            Assert.Empty(Directory.GetFiles(_directory.Path, "*.partial"));
        }
        finally
        {
            File.SetAttributes(outputPath, FileAttributes.Normal);
        }
    }

    [Fact]
    [Trait("Case", "ET-13")]
    public async Task A_successful_single_run_sort_still_replaces_an_existing_destination()
    {
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        File.WriteAllText(inputPath, "2. Banana\n1. Apple\n");
        File.WriteAllText(outputPath, "stale content from an earlier run\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        int exitCode = await SortCommand.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal("1. Apple\n2. Banana\n", File.ReadAllText(outputPath));
        Assert.Empty(Directory.GetFiles(_directory.Path, "*.partial"));
    }

    [Fact]
    [Trait("Case", "ET-14")]
    public async Task An_empty_input_sort_reports_a_destination_that_is_a_directory_by_name_and_exits_3()
    {
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        File.WriteAllBytes(inputPath, []);
        Directory.CreateDirectory(outputPath);

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        (int exitCode, string stderr) = await RunThroughConsoleRunCapturedAsync(options);

        Assert.Equal(3, exitCode);
        Assert.Contains(outputPath, stderr, StringComparison.Ordinal);
        Assert.True(Directory.Exists(outputPath));
        Assert.Empty(Directory.GetFiles(_directory.Path, "*.partial"));
    }

    [Fact]
    [Trait("Case", "ET-15")]
    public async Task A_single_run_sort_reports_a_destination_that_is_a_directory_by_name_and_exits_3()
    {
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        File.WriteAllText(inputPath, "1. Apple\n");
        Directory.CreateDirectory(outputPath);

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        (int exitCode, string stderr) = await RunThroughConsoleRunCapturedAsync(options);

        Assert.Equal(3, exitCode);
        Assert.Contains(outputPath, stderr, StringComparison.Ordinal);
        Assert.True(Directory.Exists(outputPath));
        Assert.Empty(Directory.GetFiles(_directory.Path, "*.partial"));
    }

    [Fact]
    [Trait("Case", "ET-16")]
    public async Task A_merge_that_cannot_open_its_destination_exits_5_through_ConsoleRun_with_no_stack_trace()
    {
        // Unlike the staged empty and single-run placements (exit 3), a merge opens outputPath itself.
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        WriteGeneratedInput(inputPath, targetBytes: 1024 * 1024, seed: 7);
        Directory.CreateDirectory(outputPath);

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 1_051_664, 256, Parallelism: 2, Pipeline.Channels);

        (int exitCode, string stderr) = await RunThroughConsoleRunCapturedAsync(options);

        Assert.Equal(5, exitCode);
        Assert.Contains("I/O error: ", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", stderr, StringComparison.Ordinal);
        Assert.True(Directory.Exists(outputPath));
        AssertNoLeftoverRunFiles(tempDirectory);
    }

    [Fact]
    [Trait("Case", "ET-19")]
    public async Task A_line_limit_and_parallelism_no_budget_can_support_exit_3_without_naming_a_minimum()
    {
        // At the --max-line ceiling, a million workers exceed the planner's 2^50-byte search bound.
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        File.WriteAllText(inputPath, "1. a\n");

        SorterOptions options = new(
            inputPath, outputPath, tempDirectory, 1L << 50, (int)CommandLine.MaxLineLengthCeiling, Parallelism: 1_000_000, Pipeline.Channels);

        (int exitCode, string stderr) = await RunThroughConsoleRunCapturedAsync(options);

        Assert.Equal(3, exitCode);
        Assert.Contains("No --memory budget can support --parallelism 1000000", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(outputPath));
        Assert.False(Directory.Exists(tempDirectory));
    }

    [Fact]
    [Trait("Case", "ET-17")]
    public async Task A_budget_too_small_to_plan_exits_3_naming_the_minimum_before_creating_anything()
    {
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        File.WriteAllText(inputPath, "1. a\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 1024 * 1024, 64 * 1024, Parallelism: 16, Pipeline.Channels);

        (int exitCode, string stderr) = await RunThroughConsoleRunCapturedAsync(options);

        long minimum = MemoryBudget.MinimumViableBudget(16, 64 * 1024, MemoryBudget.AssumedMeanLineLength);
        Assert.Equal(3, exitCode);
        Assert.Contains(
            $"A --memory budget of 1.0 MiB is too small. At --parallelism 16 with --max-line 64.0 KiB, the sorter needs at least {ByteSize.Describe(minimum)}.",
            stderr,
            StringComparison.Ordinal);
        Assert.False(File.Exists(outputPath));
        Assert.False(Directory.Exists(tempDirectory));
    }

    [Fact]
    [Trait("Case", "ET-18")]
    public async Task A_doubled_final_newline_is_rejected_as_an_empty_line_at_the_end_of_the_file()
    {
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        File.WriteAllText(inputPath, "2. Banana\n1. Apple\n\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);

        MalformedLineException thrown = await Assert.ThrowsAsync<MalformedLineException>(
            () => SortCommand.RunAsync(options, TestContext.Current.CancellationToken)
                .WaitAsync(BoundedWait, TestContext.Current.CancellationToken));

        Assert.Equal(3, thrown.LineNumber);
        Assert.False(File.Exists(outputPath));
    }
}
