using FileSorter.Cli;
using FileSorter.Infrastructure;
using FileSorter.LineFormat;
using FileSorter.Planning;
using FileSorter.Tests.Support;
using Xunit;
using static FileSorter.Tests.EndToEnd.SortHarness;
using static FileSorter.Tests.Support.TestTimeouts;

namespace FileSorter.Tests.EndToEnd;

// How a whole sort fails: invalid destinations, temp directories, budgets and input map
// to the documented exit codes and messages, and an existing destination is left intact.
[Collection("Program")]
public sealed class SortFailureTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task Sorting_with_a_missing_output_directory_fails_fast_before_phase_one_starts()
    {
        // A missing output directory must be caught before phase one starts: left to
        // the eventual open, it surfaces as a DirectoryNotFoundException only after
        // every run file has been produced. Exit 3 is the "invalid arguments" row.
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "no-such-directory", "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        File.WriteAllText(inputPath, "1. Apple\n2. Banana\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(3, exitCode);
        Assert.False(Directory.Exists(tempDirectory)); // TemporaryRunSet is never even constructed
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task Sorting_with_an_unusable_temp_directory_fails_fast_before_phase_one_starts()
    {
        // --temp pointed at a path that already exists as an ordinary file:
        // Directory.CreateDirectory cannot create a directory there, and the resulting
        // IOException from inside TemporaryRunSet's constructor has to map to exit 3
        // rather than escape unhandled.
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp-is-actually-a-file");
        File.WriteAllText(inputPath, "1. Apple\n2. Banana\n");
        File.WriteAllText(tempDirectory, "not a directory");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(3, exitCode);
        Assert.False(File.Exists(outputPath)); // phase one never started
    }

    [Fact]
    public async Task Sorting_a_malformed_line_surfaces_MalformedLineException_naming_the_offending_line()
    {
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");

        // Line 2 has no '.', so LineParser.TryParse rejects it: the only grammar
        // violation that does not depend on maxLineLength.
        File.WriteAllText(inputPath, "1. Apple\nno separator here\n3. Banana\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);

        MalformedLineException thrown = await Assert.ThrowsAsync<MalformedLineException>(
            () => Program.RunAsync(options, TestContext.Current.CancellationToken)
                .WaitAsync(BoundedWait, TestContext.Current.CancellationToken));

        Assert.Equal(2, thrown.LineNumber);
        Assert.Contains("no separator here", thrown.Message);
    }

    [Fact]
    [Trait("Case", "ET-11")]
    public async Task An_empty_input_sort_reports_a_read_only_existing_destination_by_name_and_exits_3()
    {
        // Windows-only: FILE_ATTRIBUTE_READONLY blocks the replace File.Move performs
        // only there. The empty-input path never opens outputPath directly, so a
        // destination this run cannot replace must be left exactly as it was, and the
        // failure to replace it is reported by name at exit 3 -- the same code and
        // message shape an unwritable output directory already gets -- rather than
        // crashing with a stack trace.
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
        // outputPath already existing forces tryMove's plain File.Move to fail (the
        // destination name is taken), which is what routes this through the
        // copy-then-move-into-place fallback in the first place; that fallback's own
        // final move is what a read-only destination then denies. Windows-only for
        // the reason given in the empty-input case above.
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
        int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal("1. Apple\n2. Banana\n", File.ReadAllText(outputPath));
        Assert.Empty(Directory.GetFiles(_directory.Path, "*.partial"));
    }

    [Fact]
    [Trait("Case", "ET-14")]
    public async Task An_empty_input_sort_reports_a_destination_that_is_a_directory_by_name_and_exits_3()
    {
        // The other shape File.Move's replace can fail on besides a read-only file: a
        // destination that is itself a directory. Portable, unlike ET-11: a rename
        // over an existing directory fails everywhere, not only under Windows sharing
        // rules.
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
    public void A_merge_that_cannot_open_its_destination_exits_5_through_ConsoleRun_with_no_stack_trace()
    {
        // The input is large enough to need a merge, which opens the output path itself,
        // and an existing directory there makes that open fail on every platform. The
        // empty and single-run shapes stage and move instead, so they report exit 3
        // (ET-14, ET-15); only the merge is a genuine I/O failure after validation.
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        WriteGeneratedInput(inputPath, targetBytes: 1024 * 1024, seed: 7);
        Directory.CreateDirectory(outputPath);

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 1_051_664, 256, Parallelism: 2, Pipeline.Channels);

        TextWriter originalError = Console.Error;
        StringWriter capturedError = new();
        Console.SetError(capturedError);
        int exitCode;
        try
        {
            exitCode = ConsoleRun.Run(ct => Program.RunAsync(options, ct));
        }
        finally
        {
            Console.SetError(originalError);
        }

        string stderr = capturedError.ToString();
        Assert.Equal(5, exitCode);
        Assert.Contains("I/O error: ", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", stderr, StringComparison.Ordinal);
        Assert.True(Directory.Exists(outputPath));
        AssertNoLeftoverRunFiles(tempDirectory);
    }

    [Fact]
    [Trait("Case", "ET-17")]
    public void A_budget_too_small_to_plan_exits_3_naming_the_minimum_before_creating_anything()
    {
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        File.WriteAllText(inputPath, "1. a\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 1024 * 1024, 64 * 1024, Parallelism: 16, Pipeline.Channels);

        TextWriter originalError = Console.Error;
        StringWriter capturedError = new();
        Console.SetError(capturedError);
        int exitCode;
        try
        {
            exitCode = ConsoleRun.Run(ct => Program.RunAsync(options, ct));
        }
        finally
        {
            Console.SetError(originalError);
        }

        long minimum = MemoryBudget.MinimumViableBudget(16, 64 * 1024, 32);
        Assert.Equal(3, exitCode);
        Assert.Contains(
            $"A --memory budget of 1.0 MiB is too small. At --parallelism 16 with --max-line 64.0 KiB, the sorter needs at least {ProgressReporter.Describe(minimum)}.",
            capturedError.ToString(),
            StringComparison.Ordinal);
        Assert.False(File.Exists(outputPath));
        Assert.False(Directory.Exists(tempDirectory));
    }

    [Fact]
    [Trait("Case", "ET-18")]
    public async Task A_doubled_final_newline_is_rejected_as_an_empty_line_at_the_end_of_the_file()
    {
        // An empty line has no separator, so it is malformed like any other line. The
        // region after a single final terminator is not a line (CB-05), but a second
        // terminator makes it one, and the sort fails on the file's very last line.
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        File.WriteAllText(inputPath, "2. Banana\n1. Apple\n\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);

        MalformedLineException thrown = await Assert.ThrowsAsync<MalformedLineException>(
            () => Program.RunAsync(options, TestContext.Current.CancellationToken)
                .WaitAsync(BoundedWait, TestContext.Current.CancellationToken));

        Assert.Equal(3, thrown.LineNumber);
        Assert.False(File.Exists(outputPath));
    }

    // For failures whose exit code ConsoleRun decides, such as a destination placement
    // cannot replace. Program.RunAsync itself lets those exceptions propagate.
    private static async Task<(int ExitCode, string Stderr)> RunThroughConsoleRunCapturedAsync(SorterOptions options)
    {
        TextWriter originalError = Console.Error;
        StringWriter capturedError = new();
        Console.SetError(capturedError);
        try
        {
            int exitCode = await Task.Run(() => ConsoleRun.Run(ct => Program.RunAsync(options, ct)))
                .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            return (exitCode, capturedError.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }
}
