using FileSorter.Cli;
using FileSorter.LineFormat;
using FileSorter.Merging;
using FileSorter.Planning;
using FileSorter.Tests.Support;
using Xunit;
using static FileSorter.Tests.EndToEnd.SortHarness;
using static FileSorter.Tests.Support.TestTimeouts;

namespace FileSorter.Tests.EndToEnd;

[Collection("Program")]
public sealed class SortTempCleanupTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sorting_a_small_input_that_fits_in_one_run_leaves_no_temp_file_behind(bool useAkka)
    {
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");

        File.WriteAllText(inputPath, "30. Cherry\n2. Apple\n100. Banana\n2. Aardvark\n");

        SorterOptions options = new(
            inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, useAkka ? Pipeline.Akka : Pipeline.Channels);
        int exitCode = await SortCommand.RunAsync(options, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        AssertIsOracleSortOfInput(inputPath, outputPath);
        AssertNoLeftoverRunFiles(tempDirectory);
    }

    [Fact]
    public async Task Cancellation_unwinds_cleanly_with_no_leftover_temp_files()
    {
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        WriteGeneratedInput(inputPath, targetBytes: 256 * 1024, seed: 3);

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SortCommand.RunAsync(options, cts.Token).WaitAsync(BoundedWait, TestContext.Current.CancellationToken));

        AssertNoLeftoverRunFiles(tempDirectory);
    }

    [Fact]
    [Trait("Case", "ET-06")]
    public async Task An_unrelated_sentinel_named_like_a_run_file_survives_a_successful_sort()
    {
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = _directory.Path;
        string sentinelPath = Path.Combine(_directory.Path, "run-00000001.tmp");
        byte[] sentinelContent = "not a run file"u8.ToArray();
        File.WriteAllBytes(sentinelPath, sentinelContent);
        File.WriteAllText(inputPath, "2. Banana\n1. Apple\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);
        int exitCode = await SortCommand.RunAsync(options, TestContext.Current.CancellationToken)
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
        int exitCode = await SortCommand.RunAsync(options, TestContext.Current.CancellationToken)
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

        // Must overlap: nothing synchronises separate TemporaryRunSet instances, so sequential runs prove nothing.
        Task<int> firstRun = SortCommand.RunAsync(firstOptions, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        Task<int> secondRun = SortCommand.RunAsync(secondOptions, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        int[] exitCodes = await Task.WhenAll(firstRun, secondRun);

        Assert.All(exitCodes, code => Assert.Equal(0, code));
        Assert.Equal("1. Apple\n2. Banana\n", File.ReadAllText(firstOutput));
        Assert.Equal("30. Cherry\n4. Date\n", File.ReadAllText(secondOutput));

        AssertNoLeftoverRunFiles(tempDirectory);
    }

    [Fact]
    [Trait("Case", "ET-09")]
    public async Task Sorting_forces_a_multi_pass_merge_and_leaves_no_private_directory_behind_on_success()
    {
        const int parallelism = 2;
        const int maxLineLength = 256;
        long budget = MemoryBudget.MinimumViableBudget(parallelism, maxLineLength, MemoryBudget.AssumedMeanLineLength);
        MemoryPlan plan = MemoryBudget.Calculate(budget, parallelism, maxLineLength, MemoryBudget.AssumedMeanLineLength);
        Assert.Equal(2, plan.MergeFanIn);

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
        Assert.True(Directory.Exists(tempDirectory));
        AssertNoLeftoverRunFiles(tempDirectory);
    }

    [Fact]
    [Trait("Case", "ET-10")]
    public async Task A_forced_failure_during_phase_one_still_removes_the_private_directory()
    {
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        File.WriteAllText(inputPath, "1. Apple\nno separator here\n3. Banana\n");

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 2L << 20, 1024, 2, Pipeline.Channels);

        await Assert.ThrowsAsync<MalformedLineException>(
            () => SortCommand.RunAsync(options, TestContext.Current.CancellationToken)
                .WaitAsync(BoundedWait, TestContext.Current.CancellationToken));

        Assert.True(Directory.Exists(tempDirectory));
        AssertNoLeftoverRunFiles(tempDirectory);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    [Trait("Case", "ET-20")]
    public async Task Cancelling_once_phase_one_has_finished_keeps_an_existing_output_and_leaves_no_run_behind()
    {
        // Cancelling on the phase-one summary line lands after every spill and before the merge opens the output.
        string inputPath = Path.Combine(_directory.Path, "in.txt");
        string outputPath = Path.Combine(_directory.Path, "out.txt");
        string tempDirectory = Path.Combine(_directory.Path, "temp");
        WriteGeneratedInput(inputPath, targetBytes: 1024 * 1024, seed: 7);
        byte[] preExisting = "a complete, correct output from an earlier run\n"u8.ToArray();
        File.WriteAllBytes(outputPath, preExisting);

        SorterOptions options = new(inputPath, outputPath, tempDirectory, 1_051_664, 256, Parallelism: 2, Pipeline.Channels);
        using CancellationTokenSource cts = new();
        using CancelOnLineWriter cancelOnPhaseOne = new("phase one produced", cts);

        (_, string stderr) = await ConsoleCapture.ErrorAsync(
            () => Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Task.Run(() => SortCommand.RunAsync(options, cts.Token), TestContext.Current.CancellationToken)
                    .WaitAsync(BoundedWait, TestContext.Current.CancellationToken)),
            cancelOnPhaseOne);

        Assert.True(ParseRunCount(stderr) > 1, "the input must spill several runs, or there is no merge to cancel");
        Assert.Equal(preExisting, File.ReadAllBytes(outputPath));
        AssertNoLeftoverRunFiles(tempDirectory);
    }

    private sealed class CancelOnLineWriter(string marker, CancellationTokenSource cts) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.Contains(marker, StringComparison.Ordinal) == true)
            {
                cts.Cancel();
            }
        }
    }
}
