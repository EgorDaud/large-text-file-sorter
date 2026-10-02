using System.Diagnostics;
using FileSorter.Infrastructure;
using FileSorter.LineFormat;
using FileSorter.Merging;
using FileSorter.Planning;
using FileSorter.Tests.Support;
using Xunit;

namespace FileSorter.Tests.Merging;

// In the "Program" collection because MP-11 captures the process-wide Console.Error.
[Collection("Program")]
public sealed class MergeExecutorTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    [Trait("Case", "MP-10")]
    public async Task The_number_of_passes_executed_matches_what_the_planner_predicted()
    {
        const int runCount = 10;
        const int fanIn = 3;

        int predictedPasses = MergePlanner.Plan(runCount, fanIn).Count;
        Assert.True(predictedPasses >= 3);

        using TemporaryRunSet runs = new(_directory.Path);
        List<string> runPaths = [];
        for (int i = 0; i < runCount; i++)
        {
            string path = runs.CreateRunPath();
            File.WriteAllText(path, $"{i}. L{i:D2}\n");
            runPaths.Add(path);
        }

        MemoryPlan plan = TestPlans.Merge(fanIn: fanIn);

        long expectedLength = runPaths.Sum(path => new FileInfo(path).Length);

        MergeExecutor executor = new(runs, plan, maxLineLength: 63);
        string outputPath = Path.Combine(_directory.Path, "output.tmp");

        await executor.ExecuteAsync(runPaths, outputPath, TestContext.Current.CancellationToken);

        Assert.Equal(predictedPasses, executor.PassesExecuted);

        Assert.Equal(predictedPasses, executor.PlannedPasses);
        Assert.Equal(expectedLength, new FileInfo(outputPath).Length);

        string[] expected = [.. Enumerable.Range(0, runCount).Select(i => $"{i}. L{i:D2}")];
        string[] lines = File.ReadAllText(outputPath).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expected, lines);

        Assert.Equal([outputPath], Directory.GetFiles(_directory.Path));
    }

    [Fact]
    [Trait("Case", "MP-11")]
    public async Task A_first_pass_run_that_cannot_be_deleted_during_a_multi_pass_merge_warns_but_neither_aborts_nor_leaks_the_run()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "A read-only attribute blocks File.Delete only on Windows.");

        // Read-only rather than a sharing hold: the merge opens every run with FileShare.None.
        const int runCount = 10;
        const int fanIn = 3;
        Assert.True(MergePlanner.Plan(runCount, fanIn).Count >= 2);

        TemporaryRunSet runs = new(_directory.Path);
        List<string> runPaths = [];
        for (int i = 0; i < runCount; i++)
        {
            string path = runs.CreateRunPath();
            File.WriteAllText(path, $"{i}. L{i:D2}\n");
            runPaths.Add(path);
        }

        string lockedRun = runPaths[0];
        string? privateDirectory = Path.GetDirectoryName(lockedRun);
        Assert.NotNull(privateDirectory);

        MergeExecutor executor = new(runs, TestPlans.Merge(fanIn, mergeParallelism: 1), maxLineLength: 63);
        string outputPath = Path.Combine(_directory.Path, "output.tmp");

        string stderr;
        File.SetAttributes(lockedRun, FileAttributes.ReadOnly);
        try
        {
            stderr = await ConsoleCapture.ErrorAsync(
                () => executor.ExecuteAsync(runPaths, outputPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.SetAttributes(lockedRun, FileAttributes.Normal);
        }

        string[] expected = [.. Enumerable.Range(0, runCount).Select(i => $"{i}. L{i:D2}")];
        Assert.Equal(expected, File.ReadAllText(outputPath).Split('\n', StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains(lockedRun, stderr);
        Assert.Single(stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), line => line.Contains(lockedRun));
        Assert.True(File.Exists(lockedRun));

        runs.Dispose();
        Assert.False(File.Exists(lockedRun));
        Assert.False(Directory.Exists(privateDirectory));
        Assert.Equal([outputPath], Directory.GetFiles(_directory.Path));
    }

    [Fact]
    public async Task A_single_run_is_rejected_rather_than_silently_doing_nothing()
    {
        using TemporaryRunSet runs = new(_directory.Path);
        string runPath = runs.CreateRunPath();
        File.WriteAllText(runPath, "1. Apple\n");

        MemoryPlan plan = TestPlans.Merge(fanIn: 2);

        MergeExecutor executor = new(runs, plan, maxLineLength: 63);
        string outputPath = Path.Combine(_directory.Path, "output.tmp");

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
            () => executor.ExecuteAsync([runPath], outputPath, TestContext.Current.CancellationToken));

        Assert.Equal("runPaths", ex.ParamName);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task Merging_throws_when_a_run_files_length_disagrees_with_its_content()
    {
        // The trailing lone CR counts toward the file length but is never written back out.
        using TemporaryRunSet runs = new(_directory.Path);
        string runA = runs.CreateRunPath();
        string runB = runs.CreateRunPath();
        File.WriteAllText(runA, "1. Apple\n3. Cherry\n\r");
        File.WriteAllText(runB, "2. Banana\n4. Date\n\r");
        List<string> runPaths = [runA, runB];

        MemoryPlan plan = TestPlans.Merge(fanIn: 2);

        MergeExecutor executor = new(runs, plan, maxLineLength: 63);
        string outputPath = Path.Combine(_directory.Path, "output.tmp");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(runPaths, outputPath, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task A_failure_during_a_non_final_pass_never_opens_the_output_file()
    {
        const int runCount = 6;
        const int fanIn = 3;
        Assert.Equal(2, MergePlanner.Plan(runCount, fanIn).Count);

        using TemporaryRunSet runs = new(_directory.Path);
        List<string> runPaths = [];
        for (int i = 0; i < runCount; i++)
        {
            string path = runs.CreateRunPath();
            File.WriteAllText(path, i == 0 ? "not a valid line\n" : $"{i}. L{i:D2}\n");
            runPaths.Add(path);
        }

        MemoryPlan plan = TestPlans.Merge(fanIn: fanIn);

        MergeExecutor executor = new(runs, plan, maxLineLength: 63);
        string outputPath = Path.Combine(_directory.Path, "output.tmp");
        byte[] preExisting = "a complete, correct output from an earlier run\n"u8.ToArray();
        File.WriteAllBytes(outputPath, preExisting);

        await Assert.ThrowsAsync<MalformedLineException>(
            () => executor.ExecuteAsync(runPaths, outputPath, TestContext.Current.CancellationToken));

        Assert.Equal(preExisting, File.ReadAllBytes(outputPath));
    }

    [Fact]
    public async Task A_multi_pass_merge_is_never_partitioned_however_many_workers_the_plan_allows()
    {
        const int runCount = 10;
        const int fanIn = 3;
        Assert.True(MergePlanner.Plan(runCount, fanIn).Count >= 3);

        using TemporaryRunSet runs = new(_directory.Path);
        List<string> runPaths = [];
        for (int i = 0; i < runCount; i++)
        {
            string path = runs.CreateRunPath();
            File.WriteAllText(path, $"{i}. L{i:D2}\n");
            runPaths.Add(path);
        }

        MergeExecutor executor = new(runs, TestPlans.Merge(fanIn, mergeParallelism: 4), maxLineLength: 63);
        string outputPath = Path.Combine(_directory.Path, "output.tmp");
        await executor.ExecuteAsync(runPaths, outputPath, TestContext.Current.CancellationToken);

        Assert.Null(executor.Partition);
        string[] expected = [.. Enumerable.Range(0, runCount).Select(i => $"{i}. L{i:D2}")];
        Assert.Equal(expected, File.ReadAllText(outputPath).Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task A_partitioned_merge_whose_worker_writes_more_than_its_slice_is_stopped_at_the_boundary()
    {
        // Each run's unterminated last line gains a '\n' on output, overrunning its slice by one byte.
        using TemporaryRunSet runs = new(_directory.Path);
        List<string> runPaths = [];
        for (int i = 0; i < 4; i++)
        {
            string path = runs.CreateRunPath();
            File.WriteAllText(path, $"{i}. Apple\n{i + 10}. Cherry");
            runPaths.Add(path);
        }

        MergeExecutor executor = new(runs, TestPlans.Merge(fanIn: 8, mergeParallelism: 3), maxLineLength: 63);
        string outputPath = Path.Combine(_directory.Path, "output.tmp");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(runPaths, outputPath, TestContext.Current.CancellationToken));

        Assert.Contains("merge worker", ex.Message, StringComparison.Ordinal);
        Assert.Equal(3, executor.Partition?.Workers);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task A_failing_partitioned_worker_surfaces_its_own_exception_and_leaves_no_run_handle_open()
    {
        // 20-byte lines; 70,002 / 20 / 79,978 keeps runB between the 4096 sampling points,
        // and index 15 is never probed by the splitter search, so only worker 2's merge reads it.
        using TemporaryRunSet runs = new(_directory.Path);
        string runA = WriteFixedWidthRun(runs, "aaa", count: 70_002);
        string runB = WriteRunBWithOneMalformedLine(runs, malformedIndex: 15);
        string runC = WriteFixedWidthRun(runs, "aaa", count: 79_978);
        List<string> runPaths = [runA, runB, runC];

        MemoryPlan plan = TestPlans.Merge(fanIn: 8, mergeParallelism: 3) with
        {
            ReadAheadBufferSize = 8192,
            ReadAheadDescriptorCapacity = 256,
        };
        MergeExecutor executor = new(runs, plan, maxLineLength: 63);
        string outputPath = Path.Combine(_directory.Path, "output.tmp");

        MalformedLineException thrown = await Assert.ThrowsAsync<MalformedLineException>(
            () => executor.ExecuteAsync(runPaths, outputPath, TestContext.Current.CancellationToken));

        Assert.Equal(300, thrown.ByteOffset); // 15 x 20
        Assert.Equal(MalformedLineException.LineNumberUnavailable, thrown.LineNumber);
        Assert.Contains(runB, thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runA, thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runC, thrown.Message, StringComparison.Ordinal);

        Assert.Equal(3, executor.Partition?.Workers);

        foreach (string path in runPaths)
        {
            File.Delete(path);
            Assert.False(File.Exists(path), $"'{path}' survived deletion, so a handle on it was still open.");
        }
    }

    [Fact]
    [Trait("Case", "SF-04")]
    public async Task A_partitioned_merge_is_byte_identical_to_the_sequential_one_and_the_output_ends_up_an_ordinary_file()
    {
        const int runCount = 6;
        const int linesPerRun = 6000;
        const int workerCount = 4;

        List<string>[] perRun = [.. Enumerable.Range(0, runCount).Select(_ => new List<string>())];
        for (int i = 0; i < runCount * linesPerRun; i++)
        {
            perRun[i % runCount].Add($"{i:D8}. run{i % runCount}\n");
        }

        string sequentialDirectory = Path.Combine(_directory.Path, "sequential");
        string partitionedDirectory = Path.Combine(_directory.Path, "partitioned");

        using TemporaryRunSet sequentialRuns = new(sequentialDirectory);
        using TemporaryRunSet partitionedRuns = new(partitionedDirectory);

        List<string> sequentialRunPaths = WriteRuns(sequentialRuns, perRun);
        List<string> partitionedRunPaths = WriteRuns(partitionedRuns, perRun);

        MergeExecutor sequential = new(sequentialRuns, TestPlans.Merge(fanIn: 128, mergeParallelism: 1), maxLineLength: 63);
        string sequentialOutput = Path.Combine(sequentialDirectory, "output.tmp");
        await sequential.ExecuteAsync(
            sequentialRunPaths, sequentialOutput, TestContext.Current.CancellationToken);

        MergeExecutor partitioned = new(partitionedRuns, TestPlans.Merge(fanIn: 128, mergeParallelism: workerCount), maxLineLength: 63);
        string partitionedOutput = Path.Combine(partitionedDirectory, "output.tmp");
        await partitioned.ExecuteAsync(
            partitionedRunPaths, partitionedOutput, TestContext.Current.CancellationToken);

        Assert.Equal(workerCount, partitioned.Partition?.Workers);
        Assert.Equal(File.ReadAllBytes(sequentialOutput), File.ReadAllBytes(partitionedOutput));

        if (OperatingSystem.IsWindows() && partitioned.Partition?.Sparse == true)
        {
            // Whether the sparse flag can be cleared depends on the filesystem, so probe this volume.
            bool clearingWorksHere = ProbeWhetherClearingSparseWorksHere(partitionedDirectory);
            bool stillSparse = File.GetAttributes(partitionedOutput).HasFlag(FileAttributes.SparseFile);
            Assert.Equal(!clearingWorksHere, stillSparse);
        }
    }

    [Fact]
    [Trait("Case", "MP-12")]
    public async Task A_partition_with_only_one_non_empty_slice_is_merged_sequentially_with_identical_bytes()
    {
        const int runCount = 6;
        const int linesPerRun = 2000;
        const int workerCount = 4;

        List<string>[] equal = [.. Enumerable.Range(0, runCount)
            .Select(_ => Enumerable.Repeat("7. same\n", linesPerRun).ToList())];
        List<string>[] distinct = [.. Enumerable.Range(0, runCount).Select(_ => new List<string>())];
        for (int i = 0; i < runCount * linesPerRun; i++)
        {
            distinct[i % runCount].Add($"{i:D8}. run{i % runCount}\n");
        }

        (byte[] equalOutput, PartitionStats? equalStats) = await MergeAtAsync(equal, workerCount, "equal-4");
        (byte[] equalSequential, PartitionStats? sequentialStats) = await MergeAtAsync(equal, 1, "equal-1");

        Assert.NotNull(equalStats);
        Assert.Equal(PartitionOutcome.SingleSlice, equalStats.Value.Outcome);
        Assert.Equal(1, equalStats.Value.Workers);
        Assert.True(double.IsPositiveInfinity(equalStats.Value.Imbalance));
        Assert.False(equalStats.Value.Sparse);
        Assert.Null(sequentialStats);
        Assert.Equal(equalSequential, equalOutput);

        string expected = string.Concat(Enumerable.Repeat("7. same\n", runCount * linesPerRun));
        Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(equalOutput));

        (byte[] distinctOutput, PartitionStats? distinctStats) = await MergeAtAsync(distinct, workerCount, "distinct-4");
        Assert.Equal(workerCount, distinctStats?.Workers);

        // The string part is the primary key, so each run's "runK" lines form one block.
        string distinctExpected = string.Concat(distinct.SelectMany(lines => lines));
        Assert.Equal(distinctExpected, System.Text.Encoding.UTF8.GetString(distinctOutput));
    }

    [Fact]
    [Trait("Case", "MP-13")]
    public async Task A_partition_with_an_empty_slice_but_several_non_empty_ones_still_merges_in_parallel()
    {
        const int runCount = 6;
        const int edgeLinesPerRun = 300;
        const int hotLinesPerRun = 1400;
        const int workerCount = 4;

        List<string>[] perRun = [.. Enumerable.Range(0, runCount).Select(_ => new List<string>())];
        List<string> below = [];
        List<string> above = [];
        for (int i = 0; i < runCount * edgeLinesPerRun; i++)
        {
            below.Add($"{i}. a{i:D6}\n");
            perRun[i % runCount].Add(below[^1]);
        }

        foreach (List<string> run in perRun)
        {
            run.AddRange(Enumerable.Repeat("5. hot\n", hotLinesPerRun));
        }

        for (int i = 0; i < runCount * edgeLinesPerRun; i++)
        {
            above.Add($"{i}. z{i:D6}\n");
            perRun[i % runCount].Add(above[^1]);
        }

        (byte[] parallelOutput, PartitionStats? parallelStats) = await MergeAtAsync(perRun, workerCount, "hot-4");
        (byte[] sequentialOutput, _) = await MergeAtAsync(perRun, 1, "hot-1");

        Assert.NotNull(parallelStats);
        Assert.Equal(PartitionOutcome.Parallel, parallelStats.Value.Outcome);
        Assert.Equal(workerCount, parallelStats.Value.Workers);
        Assert.True(double.IsPositiveInfinity(parallelStats.Value.Imbalance), "Expected the hot key to leave a slice empty.");
        Assert.Equal(sequentialOutput, parallelOutput);

        string expected = string.Concat(below)
            + string.Concat(Enumerable.Repeat("5. hot\n", runCount * hotLinesPerRun))
            + string.Concat(above);
        Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(parallelOutput));
    }

    [Theory]
    [Trait("Case", "MP-14")]
    [InlineData(1)]
    [InlineData(4)]
    public async Task An_output_hard_linked_to_another_file_is_replaced_without_writing_through_the_link(int mergeParallelism)
    {
        string directory = Path.Combine(_directory.Path, $"link-{mergeParallelism}");
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "input.txt");
        byte[] targetBytes = "2. keep\n1. me\n"u8.ToArray();
        await File.WriteAllBytesAsync(target, targetBytes, TestContext.Current.CancellationToken);
        string outputPath = Path.Combine(directory, "output.tmp");

        // A hard link needs no privilege on Windows, unlike a symlink, but .NET has no API for one.
        using (Process link = Process.Start(OperatingSystem.IsWindows()
                   ? new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/H", outputPath, target])
                   : new ProcessStartInfo("ln", [target, outputPath]))!)
        {
            await link.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.SkipUnless(link.ExitCode == 0, "A hard link could not be created here.");
        }

        List<string>[] perRun = [.. Enumerable.Range(0, 4).Select(r => Enumerable.Range(0, 500)
            .Select(i => $"{i * 4 + r:D6}. line\n").ToList())];
        using TemporaryRunSet runs = new(Path.Combine(directory, "temp"));
        MergeExecutor executor = new(runs, TestPlans.Merge(fanIn: 128, mergeParallelism), maxLineLength: 63);
        await executor.ExecuteAsync(WriteRuns(runs, perRun), outputPath, TestContext.Current.CancellationToken);

        Assert.Equal(targetBytes, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        string expected = string.Concat(perRun.SelectMany(lines => lines).OrderBy(line => line, StringComparer.Ordinal));
        Assert.Equal(expected, await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Case", "MP-15")]
    public async Task A_malformed_line_found_by_a_sequential_merge_names_its_run_file()
    {
        using TemporaryRunSet runs = new(_directory.Path);
        List<string> runPaths = WriteRuns(runs, [["1. Apple\n", "3. Cherry\n"], ["2. Banana\n", "no separator\n"], ["4. Date\n"]]);
        MergeExecutor executor = new(runs, TestPlans.Merge(fanIn: 8, mergeParallelism: 1), maxLineLength: 63);
        string outputPath = Path.Combine(_directory.Path, "output.tmp");

        MalformedLineException thrown = await Assert.ThrowsAsync<MalformedLineException>(
            () => executor.ExecuteAsync(runPaths, outputPath, TestContext.Current.CancellationToken));

        Assert.Equal(2, thrown.LineNumber);
        Assert.Equal("2. Banana\n".Length, thrown.ByteOffset);
        Assert.Contains(runPaths[1], thrown.Message, StringComparison.Ordinal);
        Assert.Null(thrown.RunIndex);
    }

    [Fact]
    [Trait("Case", "MP-16")]
    public async Task Cancelling_during_a_non_final_pass_leaves_an_existing_output_untouched_and_no_run_behind()
    {
        const int fanIn = 3;
        using TemporaryRunSet runs = new(Path.Combine(_directory.Path, "temp"));
        List<string> runPaths = WriteLargeRuns(runs, runCount: 6);
        Assert.Equal(2, MergePlanner.Plan(runPaths.Count, fanIn).Count);
        string privateDirectory = Path.GetDirectoryName(runPaths[0])!;

        MergeExecutor executor = new(runs, TestPlans.Merge(fanIn, mergeParallelism: 1), maxLineLength: 63);
        string outputPath = Path.Combine(_directory.Path, "output.tmp");
        byte[] preExisting = "a complete, correct output from an earlier run\n"u8.ToArray();
        File.WriteAllBytes(outputPath, preExisting);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CancelMidMergeAsync(
            ct => executor.ExecuteAsync(runPaths, outputPath, ct), () => executor.BytesWritten > 0));

        Assert.Equal(0, executor.PassesExecuted);
        Assert.Equal(preExisting, File.ReadAllBytes(outputPath));

        runs.Dispose();
        Assert.False(Directory.Exists(privateDirectory));
    }

    [Fact]
    [Trait("Case", "MP-17")]
    public async Task Cancelling_during_the_final_pass_removes_the_partial_output_and_leaves_no_run_behind()
    {
        const int fanIn = 3;
        using TemporaryRunSet runs = new(Path.Combine(_directory.Path, "temp"));
        List<string> runPaths = WriteLargeRuns(runs, runCount: 6);
        string privateDirectory = Path.GetDirectoryName(runPaths[0])!;

        MergeExecutor executor = new(runs, TestPlans.Merge(fanIn, mergeParallelism: 1), maxLineLength: 63);
        string outputPath = Path.Combine(_directory.Path, "output.tmp");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CancelMidMergeAsync(
            ct => executor.ExecuteAsync(runPaths, outputPath, ct), () => File.Exists(outputPath)));

        Assert.Equal(executor.PlannedPasses - 1, executor.PassesExecuted);
        Assert.True(executor.BytesWritten < executor.TotalBytesToWrite, "The merge finished before the cancel landed.");
        Assert.False(File.Exists(outputPath));

        runs.Dispose();
        Assert.False(Directory.Exists(privateDirectory));
    }

    [Fact]
    [Trait("Case", "MP-18")]
    public async Task Cancelling_a_partitioned_merge_surfaces_the_cancellation_and_leaves_no_run_handle_open()
    {
        const int workerCount = 4;
        List<string>[] perRun = [.. Enumerable.Range(0, 6).Select(_ => new List<string>())];
        for (int i = 0; i < perRun.Length * 20_000; i++)
        {
            perRun[i % perRun.Length].Add($"{i:D8}. run{i % perRun.Length}\n");
        }

        using TemporaryRunSet runs = new(Path.Combine(_directory.Path, "temp"));
        List<string> runPaths = WriteRuns(runs, perRun);
        MergeExecutor executor = new(runs, TestPlans.Merge(fanIn: 128, mergeParallelism: workerCount), maxLineLength: 63);
        string outputPath = Path.Combine(_directory.Path, "output.tmp");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CancelMidMergeAsync(
            ct => executor.ExecuteAsync(runPaths, outputPath, ct), () => executor.BytesWritten > 0));

        Assert.Equal(workerCount, executor.Partition?.Workers);
        Assert.False(File.Exists(outputPath));
        Assert.True(executor.BytesWritten < executor.TotalBytesToWrite, "The merge finished before the cancel landed.");

        foreach (string path in runPaths)
        {
            File.Delete(path);
            Assert.False(File.Exists(path), $"'{path}' survived deletion, so a handle on it was still open.");
        }
    }

    private static async Task CancelMidMergeAsync(Func<CancellationToken, Task> merge, Func<bool> when)
    {
        using CancellationTokenSource cts = new();
        Task work = Task.Run(() => merge(cts.Token), TestContext.Current.CancellationToken);
        await Task.Run(
            () =>
            {
                SpinWait spin = default;
                while (!when() && !work.IsCompleted)
                {
                    spin.SpinOnce(sleep1Threshold: -1);
                }

                cts.Cancel();
            },
            TestContext.Current.CancellationToken);

        await work.WaitAsync(TestTimeouts.BoundedWait, TestContext.Current.CancellationToken);
    }

    private static List<string> WriteLargeRuns(TemporaryRunSet runs, int runCount) =>
        [.. Enumerable.Range(0, runCount).Select(r => WriteFixedWidthRun(runs, $"r{r:D2}", count: 20_000))];

    private async Task<(byte[] Output, PartitionStats? Stats)> MergeAtAsync(
        List<string>[] perRun, int mergeParallelism, string name)
    {
        string directory = Path.Combine(_directory.Path, name);
        using TemporaryRunSet runs = new(directory);
        MergeExecutor executor = new(runs, TestPlans.Merge(fanIn: 128, mergeParallelism), maxLineLength: 63);
        string outputPath = Path.Combine(directory, "output.tmp");
        await executor.ExecuteAsync(WriteRuns(runs, perRun), outputPath, TestContext.Current.CancellationToken);

        Assert.Equal(1, executor.PassesExecuted);
        return (await File.ReadAllBytesAsync(outputPath, TestContext.Current.CancellationToken), executor.Partition);
    }

    private static List<string> WriteRuns(TemporaryRunSet runs, List<string>[] perRun)
    {
        List<string> paths = [];
        foreach (List<string> lines in perRun)
        {
            string path = runs.CreateRunPath();
            File.WriteAllText(path, string.Concat(lines));
            paths.Add(path);
        }

        return paths;
    }

    private static bool ProbeWhetherClearingSparseWorksHere(string directory)
    {
        string probePath = Path.Combine(directory, "sparse-clear-probe.bin");
        using FileStream stream = new(probePath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
        if (!SparseFile.TryMarkSparse(stream.SafeFileHandle))
        {
            return false;
        }

        const int length = 64 * 1024;
        stream.SetLength(length);
        stream.Write(new byte[length]);
        stream.Flush();
        return SparseFile.TryClearSparse(stream.SafeFileHandle);
    }

    private static string WriteFixedWidthRun(TemporaryRunSet runs, string stringPart, int count)
    {
        string path = runs.CreateRunPath();
        System.Text.StringBuilder content = new(count * 20);
        for (int i = 0; i < count; i++)
        {
            content.Append(System.Globalization.CultureInfo.InvariantCulture, $"{i:D14}. {stringPart}\n");
        }

        File.WriteAllText(path, content.ToString());
        return path;
    }

    private static string WriteRunBWithOneMalformedLine(TemporaryRunSet runs, int malformedIndex)
    {
        string path = runs.CreateRunPath();
        string malformedLine = "malformed-line".PadRight(19, '!') + "\n";
        string validLine = new string('0', 14) + ". zzz\n";
        System.Text.StringBuilder content = new(20 * 20);
        for (int i = 0; i < 20; i++)
        {
            content.Append(i == malformedIndex ? malformedLine : validLine);
        }

        File.WriteAllText(path, content.ToString());
        return path;
    }
}
