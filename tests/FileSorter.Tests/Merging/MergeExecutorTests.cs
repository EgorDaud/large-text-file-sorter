using FileSorter.LineFormat;
using FileSorter.Merging;
using FileSorter.Startup;
using Xunit;

namespace FileSorter.Tests.Merging;

// MergeExecutor is inherently a path-level type, so these tests use a real temp
// directory: the claim that the planner's prediction matches what a real multi-pass run
// does is not something a MemoryStream can stand in for.
public sealed class MergeExecutorTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "FileSorterTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Case", "MP-10")]
    public async Task The_number_of_passes_executed_matches_what_the_planner_predicted()
    {
        const int runCount = 10;
        const int fanIn = 3;

        // 10 runs at a fan-in of 3 forces the multi-pass path, driven here through a
        // real merge rather than against the planner alone.
        int predictedPasses = MergePlanner.Plan(runCount, fanIn).Count;
        Assert.True(predictedPasses >= 3);

        using TemporaryRunSet runs = new(_directory);
        List<string> runPaths = [];
        for (int i = 0; i < runCount; i++)
        {
            string path = runs.CreateRunPath();
            File.WriteAllText(path, $"{i}. L{i:D2}\n");
            runPaths.Add(path);
        }

        MemoryPlan plan = new(
            ChunkSize: 1024,
            DescriptorCapacity: 16,
            Parallelism: 1,
            MergeFanIn: fanIn,
            ReadAheadBufferSize: 65,
            ReadAheadDescriptorCapacity: 8,
            OutputBufferSize: 64,
            SpillBufferSize: 64,
            MergeParallelism: 1);

        // Run files are '\n'-normalised, so every pass carries bytes forward unchanged
        // and the final output's length is exactly the sum of the original runs'
        // lengths -- computed here before any of them are merged away, independently of
        // MergeExecutor's own preallocation arithmetic.
        long expectedLength = runPaths.Sum(path => new FileInfo(path).Length);

        MergeExecutor executor = new(runs, plan, maxLineLength: 63);
        string outputPath = Path.Combine(_directory, "output.tmp");

        await executor.ExecuteAsync(runPaths, outputPath, TestContext.Current.CancellationToken);

        Assert.Equal(predictedPasses, executor.PassesExecuted);

        // PlannedPasses lets a caller -- Program's progress reporting -- learn the same
        // count without calling MergePlanner.Plan a second time over the same inputs.
        Assert.Equal(predictedPasses, executor.PlannedPasses);
        Assert.Equal(expectedLength, new FileInfo(outputPath).Length);

        string[] expected = [.. Enumerable.Range(0, runCount).Select(i => $"{i}. L{i:D2}")];
        string[] lines = File.ReadAllText(outputPath).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expected, lines);

        // Per-group deletion is what this asserts indirectly: nothing survives in
        // the temp directory except the final output itself.
        Assert.Equal([outputPath], Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task A_single_run_is_rejected_rather_than_silently_doing_nothing()
    {
        // The single-run case belongs to Program's RunPlacement path, and
        // MergePlanner.Plan emits no passes for one run, so without this rejection
        // ExecuteAsync would be a silent no-op: it would return having written nothing
        // to outputPath, with no exception and nothing to say the caller's contract was
        // violated. Program never reaches this method with fewer than two runPaths, so
        // this states the contract in code for any other caller.
        using TemporaryRunSet runs = new(_directory);
        string runPath = runs.CreateRunPath();
        File.WriteAllText(runPath, "1. Apple\n");

        MemoryPlan plan = new(
            ChunkSize: 1024,
            DescriptorCapacity: 16,
            Parallelism: 1,
            MergeFanIn: 2,
            ReadAheadBufferSize: 65,
            ReadAheadDescriptorCapacity: 8,
            OutputBufferSize: 64,
            SpillBufferSize: 64,
            MergeParallelism: 1);

        MergeExecutor executor = new(runs, plan, maxLineLength: 63);
        string outputPath = Path.Combine(_directory, "output.tmp");

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
            () => executor.ExecuteAsync([runPath], outputPath, TestContext.Current.CancellationToken));

        Assert.Equal("runPaths", ex.ParamName);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task Merging_throws_when_a_run_files_length_disagrees_with_its_content()
    {
        // Run files are '\n'-normalised, so MergeGroupAsync's totalBytes -- summed from
        // the raw file lengths -- must equal what RunCursor delivers. The mismatch here
        // comes from a real edge: one lone, unterminated carriage return appended past
        // an otherwise well-formed file's last real line. RunCursor finalises a
        // genuinely unterminated final line by treating that byte as the first half of
        // a terminator whose '\n' never arrived, so the lone byte collapses to nothing
        // rather than an extra line -- one raw byte the merge never writes back out,
        // which is exactly the disagreement the position check at the end of
        // MergeGroupAsync exists to catch.
        using TemporaryRunSet runs = new(_directory);
        string runA = runs.CreateRunPath();
        string runB = runs.CreateRunPath();
        File.WriteAllText(runA, "1. Apple\n3. Cherry\n\r");
        File.WriteAllText(runB, "2. Banana\n4. Date\n\r");
        List<string> runPaths = [runA, runB];

        MemoryPlan plan = new(
            ChunkSize: 1024,
            DescriptorCapacity: 16,
            Parallelism: 1,
            MergeFanIn: 2,
            ReadAheadBufferSize: 65,
            ReadAheadDescriptorCapacity: 8,
            OutputBufferSize: 64,
            SpillBufferSize: 64,
            MergeParallelism: 1);

        MergeExecutor executor = new(runs, plan, maxLineLength: 63);
        string outputPath = Path.Combine(_directory, "output.tmp");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(runPaths, outputPath, TestContext.Current.CancellationToken));

        // MergeGroupAsync's guarantee stops at detecting the disagreement and throwing
        // loudly: it never deletes or truncates the destination on the way out.
        // Removing a partial output is Program's job one layer up, so this asserts only
        // what this layer guarantees -- the file it created and SetLength-preallocated
        // to the wrong, CR-inflated predicted total survives at exactly that length,
        // with the zero-filled tail its throw message warns about past the shorter
        // valid content actually written.
        long predictedTotal = runPaths.Sum(path => new FileInfo(path).Length);
        Assert.True(File.Exists(outputPath));
        Assert.Equal(predictedTotal, new FileInfo(outputPath).Length);

        // This failure is in the only, and therefore final, pass, so OutputOpened is
        // true: Program's cleanup gate would treat outputPath as worth removing here,
        // unlike the non-final-pass failure below.
        Assert.True(executor.OutputOpened);
    }

    [Fact]
    public async Task A_failure_during_a_non_final_pass_never_opens_the_output_file()
    {
        // OutputOpened is what stops Program deleting a pre-existing, valid output file
        // from an earlier run when this one fails before ever touching it. Six runs at
        // a fan-in of 3 plans exactly two passes (two groups, nothing carried forward,
        // then one group merging those two outputs); one run is malformed, in the first
        // group of the first pass, so the failure never reaches the final pass.
        const int runCount = 6;
        const int fanIn = 3;
        Assert.Equal(2, MergePlanner.Plan(runCount, fanIn).Count);

        using TemporaryRunSet runs = new(_directory);
        List<string> runPaths = [];
        for (int i = 0; i < runCount; i++)
        {
            string path = runs.CreateRunPath();
            File.WriteAllText(path, i == 0 ? "not a valid line\n" : $"{i}. L{i:D2}\n");
            runPaths.Add(path);
        }

        MemoryPlan plan = new(
            ChunkSize: 1024,
            DescriptorCapacity: 16,
            Parallelism: 1,
            MergeFanIn: fanIn,
            ReadAheadBufferSize: 65,
            ReadAheadDescriptorCapacity: 8,
            OutputBufferSize: 64,
            SpillBufferSize: 64,
            MergeParallelism: 1);

        MergeExecutor executor = new(runs, plan, maxLineLength: 63);
        string outputPath = Path.Combine(_directory, "output.tmp");
        byte[] preExisting = "a complete, correct output from an earlier run\n"u8.ToArray();
        File.WriteAllBytes(outputPath, preExisting);

        await Assert.ThrowsAsync<MalformedLineException>(
            () => executor.ExecuteAsync(runPaths, outputPath, TestContext.Current.CancellationToken));

        Assert.False(executor.OutputOpened);
        Assert.Equal(preExisting, File.ReadAllBytes(outputPath)); // untouched by this run's failure
    }

    [Fact]
    public async Task A_multi_pass_merge_is_never_partitioned_however_many_workers_the_plan_allows()
    {
        // Only a merge that is already one pass over every run is partitioned;
        // MergePlanner is not consulted about workers at all. Ten runs at a fan-in of
        // three is the same multi-pass shape as above, now with a plan that would
        // happily supply four workers, and the executor must still take the sequential
        // path and still produce the right bytes.
        const int runCount = 10;
        const int fanIn = 3;
        Assert.True(MergePlanner.Plan(runCount, fanIn).Count >= 3);

        using TemporaryRunSet runs = new(_directory);
        List<string> runPaths = [];
        for (int i = 0; i < runCount; i++)
        {
            string path = runs.CreateRunPath();
            File.WriteAllText(path, $"{i}. L{i:D2}\n");
            runPaths.Add(path);
        }

        MergeExecutor executor = new(runs, PlanWith(fanIn, mergeParallelism: 4), maxLineLength: 63);
        string outputPath = Path.Combine(_directory, "output.tmp");
        await executor.ExecuteAsync(runPaths, outputPath, TestContext.Current.CancellationToken);

        Assert.Equal(1, executor.MergeParallelismUsed);
        Assert.Equal(0, executor.SplitterSeconds);   // the partitioner never ran
        string[] expected = [.. Enumerable.Range(0, runCount).Select(i => $"{i}. L{i:D2}")];
        Assert.Equal(expected, File.ReadAllText(outputPath).Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task A_partitioned_merge_whose_worker_writes_more_than_its_slice_is_stopped_at_the_boundary()
    {
        // The partitioned counterpart of the length check above. The per-worker and
        // aggregate length checks and OutputSliceStream's end-offset guard are one
        // combined defence rather than two independent ones; this exercises the guard.
        //
        // The fixture that provokes it is a run whose last line has no trailing '\n'.
        // RangePartitioner's alignment recognises there is no complete line past the
        // last real '\n' and never samples or locates a splitter inside that tail, but
        // every run's partition still extends to the run's raw byte length, so whichever
        // worker's slice reaches the tail delivers it as its own final line and writes
        // one byte MORE than its predicted slice length: the content plus the '\n' the
        // merge always adds, against a slice that never had one. OutputSliceStream's
        // guard catches that the instant the extra byte would cross into the next range,
        // before the per-worker length check downstream gets a chance to run.
        //
        // A lone, unterminated '\r' cannot be used here instead: the partitioner's
        // sampling treats the byte right after a real '\n' as a candidate line start
        // regardless of what follows, so it would try to parse the orphan '\r' as a line
        // and throw MalformedLineException before the merge ever ran.
        using TemporaryRunSet runs = new(_directory);
        List<string> runPaths = [];
        for (int i = 0; i < 4; i++)
        {
            string path = runs.CreateRunPath();
            File.WriteAllText(path, $"{i}. Apple\n{i + 10}. Cherry");
            runPaths.Add(path);
        }

        MergeExecutor executor = new(runs, PlanWith(fanIn: 8, mergeParallelism: 3), maxLineLength: 63);
        string outputPath = Path.Combine(_directory, "output.tmp");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(runPaths, outputPath, TestContext.Current.CancellationToken));

        Assert.Contains("merge worker", ex.Message, StringComparison.Ordinal);
        Assert.Equal(3, executor.MergeParallelismUsed);
        Assert.True(executor.OutputOpened);   // Program's own cleanup gate: this run did touch outputPath
    }

    [Fact]
    public async Task A_failing_partitioned_worker_surfaces_its_own_exception_and_leaves_no_run_handle_open()
    {
        // RootCause's job is to pick the one real failure out of an aggregate that also
        // holds the OperationCanceledExceptions this call's cancellation-of-siblings
        // produces. A fixture that fails every worker for the same reason at the same
        // instant does not exercise it, because the aggregate then holds no cancellation
        // to skip over. So exactly ONE worker fails on real, malformed data here while
        // the other two merge tens of thousands of genuine lines to completion.
        //
        // The malformed line has to reach the merge without RangePartitioner.Locate ever
        // touching it: Locate parses every line it reads, sampled or probed, and would
        // throw MalformedLineException itself before a single worker started. Its exact
        // position is therefore chosen against Locate's two searches rather than left to
        // chance:
        //
        //  - Sampling draws TargetSampleLines (4096) lines at byte positions stratified
        //    across the three runs' concatenated bytes. `runB` (the run holding the
        //    malformed line) is placed as a 400-byte island inside a 3,000,000-byte total
        //    (150,000 lines x 20 bytes, split 70,002 / 20 / 79,978 across runA / runB /
        //    runC), positioned so that none of the 4096 stratified points -- computed by
        //    the same ((2i+1) x total) / (2 x 4096) RangePartitioner itself uses -- falls
        //    inside runB's byte range: the nearest points sit 16 bytes before it and 316
        //    bytes after, comfortably outside it on both sides.
        //  - Offset search locates each of the two splitters (three workers) with a binary
        //    search per run. runB's 19 well-formed lines all read "zzz", lexicographically
        //    above every splitter (which are drawn only from runA/runC's "aaa" domain,
        //    since sampling never touches runB) -- so every probe inside runB reads as
        //    "at or above" and the search converges toward offset zero by repeated halving,
        //    touching only byte positions 200, 100, 50, 25, 12, 6, 3, 1 and 0, which align
        //    forward to line indices {0, 1, 2, 3, 5, 10} of runB's 20 lines. The malformed
        //    line sits at index 15 -- outside that set -- so both offset searches, and the
        //    sampling above, read only well-formed content. A run that reads "at or above"
        //    everywhere lands wholly in the last worker, so runB's entire 400 bytes end up
        //    in worker 2's slice, which is where the actual merge -- reading every line in
        //    its slice, not just the probed ones -- reaches the malformed line and fails
        //    for real.
        //
        // Workers 0 and 1 get no part of runB at all (its whole 400 bytes resolve to
        // worker 2's slice), so they merge their own genuine slices of runA and runC to
        // completion -- real work, not a fast no-op -- and are then cancelled by worker 2's
        // failure while still awaiting their own I/O, which is exactly the sibling
        // OperationCanceledException RootCause has to look past.
        using TemporaryRunSet runs = new(_directory);
        // Every line across all three runs below is exactly 20 bytes, "\n" included.
        string runA = WriteFixedWidthRun(runs, "aaa", count: 70_002);
        string runB = WriteRunBWithOneMalformedLine(runs, malformedIndex: 15);
        string runC = WriteFixedWidthRun(runs, "aaa", count: 79_978);
        List<string> runPaths = [runA, runB, runC];

        // Read-ahead buffers sized for real throughput over tens of thousands of lines.
        // PlanWith's own tiny defaults exist to make small hand-written fixtures
        // exercise capacity limits, not to merge this much data quickly.
        MemoryPlan plan = PlanWith(fanIn: 8, mergeParallelism: 3) with
        {
            ReadAheadBufferSize = 8192,
            ReadAheadDescriptorCapacity = 256,
        };
        MergeExecutor executor = new(runs, plan, maxLineLength: 63);
        string outputPath = Path.Combine(_directory, "output.tmp");

        // Catching this exact type rather than OperationCanceledException or an
        // AggregateException is what confirms MergeExecutor.RootCause picked the real
        // failure out of the two siblings' cancellations rather than surfacing
        // whichever Task.WhenAll happened to see first.
        MalformedLineException thrown = await Assert.ThrowsAsync<MalformedLineException>(
            () => executor.ExecuteAsync(runPaths, outputPath, TestContext.Current.CancellationToken));

        // The exception has to name the run file it came from: an offset alone is
        // indistinguishable from a malformed line in any other run this merge touched,
        // and describes a position an operator has no file to open. runB's whole 400
        // bytes resolve to worker 2's slice starting at its own offset 0, so the
        // slice-relative and absolute offsets coincide numerically here: line index 15
        // at 20 bytes a line is byte offset 300.
        Assert.Equal(300, thrown.ByteOffset);
        Assert.Equal(MalformedLineException.LineNumberUnavailable, thrown.LineNumber);
        Assert.Contains(runB, thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runA, thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runC, thrown.Message, StringComparison.Ordinal);

        // The type check alone cannot distinguish "the partition never located, and
        // RangePartitioner.Locate threw this same exception type itself" from "the
        // partition succeeded and worker 2 hit the malformed line during its own real
        // merge" -- both surface a MalformedLineException. MergeParallelismUsed is set
        // to the real worker count the instant Locate returns a partition, before any
        // worker starts, and stays at its ExecuteAsync-entry value of 1 if Locate is
        // what failed, so it is what confirms the partition was located and three
        // workers started.
        Assert.Equal(3, executor.MergeParallelismUsed);

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
        // Round-robin assignment across runCount buckets, over a strictly increasing
        // counter, is what keeps every run individually sorted (each bucket receives a
        // strictly increasing subsequence) while still spreading the key space across
        // every run -- the shape RangePartitioner needs to locate a real, multi-worker
        // partition rather than degenerate to one worker holding everything.
        const int runCount = 6;
        const int linesPerRun = 6000;
        const int workerCount = 4;

        List<string>[] perRun = [.. Enumerable.Range(0, runCount).Select(_ => new List<string>())];
        for (int i = 0; i < runCount * linesPerRun; i++)
        {
            perRun[i % runCount].Add($"{i:D8}. run{i % runCount}\n");
        }

        string sequentialDirectory = Path.Combine(_directory, "sequential");
        string partitionedDirectory = Path.Combine(_directory, "partitioned");

        using TemporaryRunSet sequentialRuns = new(sequentialDirectory);
        using TemporaryRunSet partitionedRuns = new(partitionedDirectory);

        // Two independent copies of the same content, since MergeExecutor deletes its
        // own run inputs as it goes -- one merge's runs cannot be reused for the other.
        List<string> sequentialRunPaths = WriteRuns(sequentialRuns, perRun);
        List<string> partitionedRunPaths = WriteRuns(partitionedRuns, perRun);

        MergeExecutor sequential = new(sequentialRuns, PlanWith(fanIn: 128, mergeParallelism: 1), maxLineLength: 63);
        string sequentialOutput = Path.Combine(sequentialDirectory, "output.tmp");
        await sequential.ExecuteAsync(
            sequentialRunPaths, sequentialOutput, TestContext.Current.CancellationToken);

        MergeExecutor partitioned = new(partitionedRuns, PlanWith(fanIn: 128, mergeParallelism: workerCount), maxLineLength: 63);
        string partitionedOutput = Path.Combine(partitionedDirectory, "output.tmp");
        await partitioned.ExecuteAsync(
            partitionedRunPaths, partitionedOutput, TestContext.Current.CancellationToken);

        // Confirms the partitioned path was actually taken rather than falling back to
        // the sequential one; without this, byte-identity alone would not distinguish
        // "the partition ran and agreed" from "the partition never ran at all."
        Assert.Equal(workerCount, partitioned.MergeParallelismUsed);
        Assert.Equal(File.ReadAllBytes(sequentialOutput), File.ReadAllBytes(partitionedOutput));

        if (OperatingSystem.IsWindows() && partitioned.OutputMarkedSparse)
        {
            // Whether the finished file still carries the sparse attribute depends on
            // whether this machine's filesystem allows clearing it once every byte has
            // been written -- verified separately (SF-03) to succeed on NTFS, but not
            // asserted as a universal fact here. Probing it independently, against a
            // throwaway file on the same volume the real merge just wrote to, is what
            // lets this assertion hold on any machine the suite runs on rather than
            // pinning one filesystem's behaviour.
            bool clearingWorksHere = ProbeWhetherClearingSparseWorksHere(partitionedDirectory);
            bool stillSparse = File.GetAttributes(partitionedOutput).HasFlag(FileAttributes.SparseFile);
            Assert.Equal(!clearingWorksHere, stillSparse);
        }
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

    // A fully-written sparse file, marked and cleared the same way MergeExecutor marks
    // and clears its own output, on the same volume as the real merge under test. Its
    // result is what a machine-specific filesystem behaviour looks like from outside
    // SparseFile itself, without exposing a third public member on SparseFile purely for
    // this probe.
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

    // `count` lines of exactly 20 bytes each ("{number:D14}. {stringPart}\n"), sorted by
    // construction since the number climbs and the string part is fixed.
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

    // Twenty 20-byte lines reading "zzz" -- lexicographically above every splitter the
    // other two runs can produce -- except `malformedIndex`, which is 20 bytes of no
    // valid line at all. The test above derives why index 15 is the one position the
    // partitioner's searches never read.
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

    private static MemoryPlan PlanWith(int fanIn, int mergeParallelism) => new(
        ChunkSize: 1024,
        DescriptorCapacity: 16,
        Parallelism: 1,
        MergeFanIn: fanIn,
        ReadAheadBufferSize: 65,
        ReadAheadDescriptorCapacity: 8,
        OutputBufferSize: 64,
        SpillBufferSize: 64,
        MergeParallelism: mergeParallelism);
}
