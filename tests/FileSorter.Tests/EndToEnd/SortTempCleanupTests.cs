using FileSorter.Cli;
using FileSorter.LineFormat;
using FileSorter.Merging;
using FileSorter.Planning;
using FileSorter.Tests.Support;
using Xunit;
using static FileSorter.Tests.EndToEnd.SortHarness;
using static FileSorter.Tests.Support.TestTimeouts;

namespace FileSorter.Tests.EndToEnd;

// The temporary-file contract of a whole sort: run files and the private directory are
// gone after success, failure and cancellation, and nothing outside them is touched.
[Collection("Program")]
public sealed class SortTempCleanupTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    // Pipeline is internal, and a [Theory]'s parameters must be as accessible as
    // the public test method itself, so the pipeline choice travels as bool here
    // (true means Akka) and is mapped back just before use.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sorting_a_small_input_that_fits_in_one_run_leaves_no_temp_file_behind(bool useAkka)
    {
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");

        // A handful of lines is nowhere near ChunkSize at this budget, so ChunkReader
        // emits exactly one chunk, phase one emits exactly one run, and phase two takes
        // RunPlacement's single-run branch rather than MergeExecutor.
        File.WriteAllText(inputPath, "30. Cherry\n2. Apple\n100. Banana\n2. Aardvark\n");

        SorterOptions options = new(
            inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, useAkka ? Pipeline.Akka : Pipeline.Channels);
        int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        AssertIsSortedPermutationOfInput(inputPath, outputPath);
        AssertNoLeftoverRunFiles(tempDirectory);
    }

    [Fact]
    public async Task Cancellation_unwinds_cleanly_with_no_leftover_temp_files()
    {
        // This exercises the CancellationToken plumbing through RunAsync, not the
        // exit-130 mapping: that mapping lives in ConsoleRun (CR-04), and no OS-level
        // Ctrl+C can be delivered to a process from inside a unit test.
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        WriteGeneratedInput(inputPath, targetBytes: 256 * 1024, seed: 3);

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Program.RunAsync(options, cts.Token).WaitAsync(BoundedWait, TestContext.Current.CancellationToken));

        AssertNoLeftoverRunFiles(tempDirectory);
    }

    [Fact]
    [Trait("Case", "ET-06")]
    public async Task An_unrelated_sentinel_named_like_a_run_file_survives_a_successful_sort()
    {
        // Run files must never share a namespace with anything a user could plausibly
        // have sitting next to the output, including a name matching the run-file
        // pattern this codebase's own runs take.
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = _directory.Path;
        string sentinelPath = Path.Combine(_directory.Path, "run-00000001.tmp");
        byte[] sentinelContent = "not a run file"u8.ToArray();
        File.WriteAllBytes(sentinelPath, sentinelContent);
        File.WriteAllText(inputPath, "2. Banana\n1. Apple\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal("1. Apple\n2. Banana\n", File.ReadAllText(outputPath));
        Assert.Equal(sentinelContent, File.ReadAllBytes(sentinelPath));
    }

    [Fact]
    [Trait("Case", "ET-07")]
    public async Task An_output_named_like_a_run_file_is_produced_correctly()
    {
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "run-00000001.tmp");
        string tempDirectory = _directory.Path;
        File.WriteAllText(inputPath, "2. Banana\n1. Apple\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(outputPath));
        Assert.Equal("1. Apple\n2. Banana\n", File.ReadAllText(outputPath));
    }

    [Fact]
    [Trait("Case", "ET-08")]
    public async Task Two_concurrent_sorts_sharing_one_temp_parent_do_not_disturb_each_other()
    {
        string tempDirectory = Path.Combine(_directory.Path, "shared-temp");

        string firstInput = Path.Combine(_directory.Path, "in1.txt");
        string firstOutput = Path.Combine(_directory.Path, "out1.txt");
        File.WriteAllText(firstInput, "2. Banana\n1. Apple\n");

        string secondInput = Path.Combine(_directory.Path, "in2.txt");
        string secondOutput = Path.Combine(_directory.Path, "out2.txt");
        File.WriteAllText(secondInput, "30. Cherry\n4. Date\n");

        SorterOptions firstOptions = new(firstInput, firstOutput, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        SorterOptions secondOptions = new(secondInput, secondOutput, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);

        // Nothing synchronises two different TemporaryRunSet instances against each
        // other, only the workers inside one, so two real, concurrently running
        // invocations are what actually exercises the private-directory-per-instance
        // guarantee -- two sequential ones would not.
        Task<int> firstRun = Program.RunAsync(firstOptions, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        Task<int> secondRun = Program.RunAsync(secondOptions, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        int[] exitCodes = await Task.WhenAll(firstRun, secondRun);

        Assert.All(exitCodes, code => Assert.Equal(0, code));
        Assert.Equal("1. Apple\n2. Banana\n", File.ReadAllText(firstOutput));
        Assert.Equal("30. Cherry\n4. Date\n", File.ReadAllText(secondOutput));

        // Both invocations shared tempDirectory, so this proves neither leaked a
        // private directory nor a run file into the other's namespace.
        AssertNoLeftoverRunFiles(tempDirectory);
    }

    [Fact]
    [Trait("Case", "ET-09")]
    public async Task Sorting_forces_a_multi_pass_merge_and_leaves_no_private_directory_behind_on_success()
    {
        // The plan at the minimum viable budget pins MergeFanIn at MinMergeFanIn (2)
        // -- the bare minimum a viable budget can give -- so any run count above two
        // forces several merge passes rather than one, the same technique ET-04's own
        // multi-pass shape uses. The input needs to be large enough, at this budget's
        // tiny chunk size, to produce more than two runs.
        const int parallelism = 2;
        const int maxLineLength = 256;
        long budget = MemoryBudget.MinimumViableBudget(parallelism, maxLineLength, assumedMeanLineLength: 32);
        MemoryPlan plan = MemoryBudget.Calculate(budget, parallelism, maxLineLength, assumedMeanLineLength: 32);
        Assert.Equal(2, plan.MergeFanIn); // MinMergeFanIn -- the whole point of using the bare minimum

        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        WriteGeneratedInput(inputPath, targetBytes: 256 * 1024, seed: 5);

        SorterOptions options = new(inputPath, outputPath, tempDirectory, budget, maxLineLength, parallelism, Pipeline.Channels);
        (int exitCode, string stderr) = await RunCapturedAsync(options);
        int runCount = ParseRunCount(stderr);
        int passes = MergePlanner.Plan(runCount, plan.MergeFanIn).Count;

        Assert.Equal(0, exitCode);
        Assert.True(runCount > 2, "the fixture must produce more runs than the fan-in, or a fan-in of 2 cannot force more than one pass");
        Assert.True(passes > 1, "this shape must force MergePlanner to run more than one pass, or it is no different from a single-pass merge");
        // The sorter leaves the parent alone and removes only its own private
        // subdirectory, so --temp survives success -- empty.
        Assert.True(Directory.Exists(tempDirectory));
        AssertNoLeftoverRunFiles(tempDirectory);
    }

    [Fact]
    [Trait("Case", "ET-10")]
    public async Task A_forced_failure_during_phase_one_still_removes_the_private_directory()
    {
        // MalformedLineException surfaces after some runs may already have been
        // spilled, so this is the case where the private directory genuinely held
        // files at the moment of failure, not merely an empty directory nobody wrote
        // into. TemporaryRunSet's cleanup runs from RunAsync's `using` declaration
        // regardless of how phase one exits.
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        File.WriteAllText(inputPath, "1. Apple\nno separator here\n3. Banana\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);

        await Assert.ThrowsAsync<MalformedLineException>(
            () => Program.RunAsync(options, TestContext.Current.CancellationToken)
                .WaitAsync(BoundedWait, TestContext.Current.CancellationToken));

        // The sorter leaves the parent alone and removes only its own private
        // subdirectory, so --temp survives the failure -- empty.
        Assert.True(Directory.Exists(tempDirectory));
        AssertNoLeftoverRunFiles(tempDirectory);
        Assert.False(File.Exists(outputPath));
    }

    // The same Console.Error swap SortCapturedAsync uses, without that helper's own
    // assumption that the run succeeds: a caller here wants the exit code and the
    // stderr text for a run that may fail by design.
    private static async Task<(int ExitCode, string Stderr)> RunCapturedAsync(SorterOptions options)
    {
        TextWriter originalError = Console.Error;
        StringWriter capturedError = new();
        Console.SetError(capturedError);
        try
        {
            int exitCode = await Program.RunAsync(options, TestContext.Current.CancellationToken)
                .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
            return (exitCode, capturedError.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }
}
