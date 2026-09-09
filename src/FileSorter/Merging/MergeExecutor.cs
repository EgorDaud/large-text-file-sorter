using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using FileSorter.LineFormat;
using FileSorter.Startup;

namespace FileSorter.Merging;

internal sealed class MergeExecutor
{
    private readonly TemporaryRunSet _runs;
    private readonly MemoryPlan _plan;
    private readonly int _maxLineLength;

    // One pair of read-ahead windows and descriptor array per possible cursor. Allocate in
    // ExecuteAsync for the actual fan-in so MemoryPlan bounds each call's real allocation.
    private RunCursorBuffers[] _cursorBuffers = [];

    // Reused output staging buffer with the fixed MemoryPlan size.
    private readonly byte[] _outputStagingBuffer;

    // Shared across groups and passes for whole-merge progress totals.
    private readonly MergeProgress _progress = new();

    // Cached ArraySegment views avoid allocating a buffer-array slice for each group.
    private ArraySegment<RunCursorBuffers>[] _cursorBufferSegments = [];

    public MergeExecutor(TemporaryRunSet runs, MemoryPlan plan, int maxLineLength)
    {
        _runs = runs;
        _plan = plan;
        _maxLineLength = maxLineLength;
        _outputStagingBuffer = new byte[plan.OutputBufferSize];
    }

    public int PassesExecuted { get; private set; }

    // Planned pass count; zero before ExecuteAsync.
    public int PlannedPasses { get; private set; }

    // True once outputPath is opened, allowing callers to decide whether failure cleanup
    // should delete it.
    public bool OutputOpened { get; private set; }

    // Bytes written across all groups and passes; safe for concurrent progress reads.
    public long BytesWritten => _progress.BytesWritten;

    // Exact bytes across all planned passes; zero before ExecuteAsync.
    public long TotalBytesToWrite { get; private set; }

    // Output-write wait time summed across all workers and passes.
    public double OutputWaitSeconds => _progress.OutputWaitSeconds;

    // Partition workers used, or 1 for sequential fallback; zero before ExecuteAsync.
    public int MergeParallelismUsed { get; private set; }

    // Time spent locating a partition; zero on the sequential path.
    public double SplitterSeconds { get; private set; }

    // Largest worker slice divided by the smallest, or 0 on the sequential path.
    public double PartitionImbalance { get; private set; }

    // True when the partitioned output was marked sparse before SetLength. Unsupported
    // filesystems use the same correct merge path without sparse allocation.
    public bool OutputMarkedSparse { get; private set; }

    // Fastest and slowest worker times. Their gap can reveal device stragglers beyond byte
    // imbalance; both are zero on the sequential path.
    public double SlowestWorkerSeconds { get; private set; }

    public double QuickestWorkerSeconds { get; private set; }

    public async Task ExecuteAsync(IReadOnlyList<string> runPaths, string outputPath, CancellationToken ct)
    {
        // A single run belongs to RunPlacement; MergePlanner would produce no output pass.
        if (runPaths.Count < 2)
        {
            throw new ArgumentException(
                $"MergeExecutor merges two or more runs; a single run is moved into place by Program without a merge. Got {runPaths.Count}.",
                nameof(runPaths));
        }

        IReadOnlyList<MergePass> passes = MergePlanner.Plan(runPaths.Count, _plan.MergeFanIn);
        PlannedPasses = passes.Count;
        TotalBytesToWrite = ComputeTotalBytesToWrite(runPaths, passes);
        MergeParallelismUsed = 1;

        // Partition only a one-pass merge over every run. Multi-pass partitioning requires
        // intermediate runs that do not yet exist.
        if (_plan.MergeParallelism > 1 && IsOnePassOverEveryRun(passes)
            && await TryMergePartitionedAsync(runPaths, outputPath, ct))
        {
            PassesExecuted = 1;
            return;
        }

        // No group exceeds the smaller of run count and planned fan-in.
        int fanIn = Math.Min(runPaths.Count, _plan.MergeFanIn);

        _cursorBuffers = new RunCursorBuffers[fanIn];
        for (int i = 0; i < fanIn; i++)
        {
            _cursorBuffers[i] = new RunCursorBuffers(
                new byte[_plan.ReadAheadBufferSize],
                new byte[_plan.ReadAheadBufferSize],
                new LineDescriptor[_plan.ReadAheadDescriptorCapacity]);
        }

        _cursorBufferSegments = new ArraySegment<RunCursorBuffers>[fanIn];
        for (int i = 0; i < fanIn; i++)
        {
            _cursorBufferSegments[i] = new ArraySegment<RunCursorBuffers>(_cursorBuffers, 0, i + 1);
        }

        List<string> current = [.. runPaths];

        for (int passIndex = 0; passIndex < passes.Count; passIndex++)
        {
            MergePass pass = passes[passIndex];

            // The final pass has one group and no carried run, so it writes directly to outputPath.
            bool finalPass = passIndex == passes.Count - 1;
            List<string> next = [];

            foreach (int[] group in pass.Groups)
            {
                string destination = finalPass ? outputPath : _runs.CreateRunPath();
                await MergeGroupAsync(group, current, destination, isOutputDestination: finalPass, ct);

                // Delete each merged group promptly to keep temporary usage near input size.
                foreach (int index in group)
                {
                    _runs.Delete(current[index]);
                }

                next.Add(destination);
            }

            foreach (int index in pass.CarriedForward)
            {
                next.Add(current[index]);
            }

            current = next;
            PassesExecuted++;
        }
    }

    // One pass, one group, and no carried run means the group contains every run.
    private static bool IsOnePassOverEveryRun(IReadOnlyList<MergePass> passes) =>
        passes.Count == 1 && passes[0].Groups.Count == 1 && passes[0].CarriedForward.Count == 0;

    // Locates key ranges and merges them independently into disjoint preallocated output
    // ranges. Returns false without writing when no partition can be sampled.
    private async Task<bool> TryMergePartitionedAsync(
        IReadOnlyList<string> runPaths, string outputPath, CancellationToken ct)
    {
        int workers = _plan.MergeParallelism;

        Stopwatch splitClock = Stopwatch.StartNew();
        RangePartition? located = RangePartitioner.Locate(runPaths, workers, _maxLineLength, _plan.Parallelism, ct);
        splitClock.Stop();
        SplitterSeconds = splitClock.Elapsed.TotalSeconds;

        if (located is not { } partition)
        {
            return false;
        }

        MergeParallelismUsed = workers;
        PartitionImbalance = partition.Imbalance;

        // Preallocate once so worker offsets are stable and no worker extends the file.
        using (FileStream preallocate = new(
                   outputPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, bufferSize: 1))
        {
            // The output path is now touched even if later setup fails.
            OutputOpened = true;

            // Mark sparse before SetLength so nonzero-offset workers create holes instead
            // of forcing NTFS to zero-fill up to their first write. Failure is optional and
            // leaves the ordinary preallocated-file behavior.
            OutputMarkedSparse = SparseFile.TryMarkSparse(preallocate.SafeFileHandle);
            preallocate.SetLength(partition.TotalBytes);
        }

        // Cancel sibling workers after a failure, then preserve the original non-cancellation
        // cause rather than a sibling's resulting cancellation.
        using CancellationTokenSource workerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long[] written = new long[workers];
        double[] seconds = new double[workers];
        Task[] tasks = new Task[workers];
        for (int w = 0; w < workers; w++)
        {
            int worker = w;
            tasks[w] = Task.Run(
                async () =>
                {
                    Stopwatch clock = Stopwatch.StartNew();
                    try
                    {
                        written[worker] = await MergeSliceAsync(
                            runPaths, outputPath, partition, worker, workerCts.Token);
                    }
                    catch
                    {
                        await workerCts.CancelAsync();
                        throw;
                    }
                    finally
                    {
                        seconds[worker] = clock.Elapsed.TotalSeconds;
                    }
                },
                workerCts.Token);
        }

        // Wait for every worker to release its shared-read run handles before deleting runs.
        Task all = Task.WhenAll(tasks);
        try
        {
            await all;
        }
        catch when (all.IsFaulted)
        {
            ExceptionDispatchInfo.Capture(RootCause(all.Exception!, ct)).Throw();
            throw;
        }

        QuickestWorkerSeconds = seconds.Min();
        SlowestWorkerSeconds = seconds.Max();

        // Per-worker and aggregate checks, with OutputSliceStream's bound, detect writes
        // into a neighboring range that a whole-file length check misses.
        long total = 0;
        for (int w = 0; w < workers; w++)
        {
            if (written[w] != partition.SliceBytes[w])
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                    $"Merge worker {w} wrote {written[w]} bytes to '{outputPath}' but its slice is " +
                    $"{partition.SliceBytes[w]} bytes."));
            }

            total += written[w];
        }

        if (total != partition.TotalBytes)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"The partitioned merge wrote {total} bytes to '{outputPath}' but predicted {partition.TotalBytes}."));
        }

        // Checks prove every byte is written, so clearing sparse is metadata-only. A failure
        // to reopen or clear leaves a correct output and must not fail the sort.
        if (OutputMarkedSparse)
        {
            try
            {
                using FileStream final = new(outputPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                SparseFile.TryClearSparse(final.SafeFileHandle);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        foreach (string path in runPaths)
        {
            _runs.Delete(path);
        }

        return true;
    }

    // Merges every run's slice into one worker output range. Workers use FileShare.Read so
    // all can open each run; Task.WhenAll completes before run deletion, after KWayMerge
    // and this method dispose their handles.
    private async Task<long> MergeSliceAsync(
        IReadOnlyList<string> runPaths, string outputPath, RangePartition partition, int worker, CancellationToken ct)
    {
        long[][] offsets = partition.RunOffsets;
        List<Stream> inputs = new(runPaths.Count);
        List<RunCursorBuffers> buffers = new(runPaths.Count);

        // Parallel to inputs and buffers for mapping cursor indices back to non-empty slices.
        List<(string Path, long SliceStart)> inputRuns = new(runPaths.Count);
        FileStream? destination = null;
        OutputSliceStream? output = null;
        byte[] staging;
        try
        {
            for (int r = 0; r < runPaths.Count; r++)
            {
                long start = offsets[r][worker];
                long end = offsets[r][worker + 1];

                // Empty slices need no handle, cursor, or buffers.
                if (end <= start)
                {
                    continue;
                }

                // Cursor read-ahead buffers already provide buffering.
                FileStream file = new(
                    runPaths[r], FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                inputs.Add(new RunSliceStream(file, start, end));
                buffers.Add(new RunCursorBuffers(
                    new byte[_plan.ReadAheadBufferSize],
                    new byte[_plan.ReadAheadBufferSize],
                    new LineDescriptor[_plan.ReadAheadDescriptorCapacity]));
                inputRuns.Add((runPaths[r], start));
            }

            long sliceStart = partition.OutputOffsets[worker];
            destination = new FileStream(
                outputPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 1,
                FileOptions.Asynchronous);
            output = new OutputSliceStream(destination, sliceStart, sliceStart + partition.SliceBytes[worker]);

            // Worker 0 reuses the executor buffer; others allocate one each, matching the
            // MemoryPlan worker count. Allocate under cleanup protection.
            staging = worker == 0 ? _outputStagingBuffer : new byte[_plan.OutputBufferSize];
        }
        catch
        {
            // This method still owns all streams until KWayMerge begins.
            foreach (Stream input in inputs)
            {
                input.Dispose();
            }

            // Output owns destination once constructed; otherwise dispose destination directly.
            if (output is not null)
            {
                output.Dispose();
            }
            else
            {
                destination?.Dispose();
            }

            throw;
        }

        await using (output)
        {
            try
            {
                await KWayMerge.MergeAsync(inputs, buffers, output, staging, _maxLineLength, _progress, ct);
            }
            catch (MalformedLineException ex) when (ex.RunIndex is { } index && index < inputRuns.Count)
            {
                // Convert the slice-relative byte offset to the run-file offset. The worker
                // cannot determine an absolute line number because it starts mid-run.
                (string path, long sliceStart) = inputRuns[index];
                throw new MalformedLineException(
                    sliceStart + ex.ByteOffset, MalformedLineException.LineNumberUnavailable, ex.Preview, path);
            }
        }

        return output.BytesWritten;
    }

    // Preserve a real worker failure over cancellations triggered for sibling shutdown,
    // except when the caller cancelled its own token.
    private static Exception RootCause(AggregateException aggregate, CancellationToken ct)
    {
        ReadOnlyCollection<Exception> failures = aggregate.Flatten().InnerExceptions;
        if (ct.IsCancellationRequested)
        {
            return failures[0];
        }

        foreach (Exception failure in failures)
        {
            if (failure is not OperationCanceledException)
            {
                return failure;
            }
        }

        return failures[0];
    }

    // Computes exact bytes across passes. Each run line has one LF terminator, so input and
    // output sizes match; MergePlanner can therefore be applied to sizes before reading.
    private static long ComputeTotalBytesToWrite(IReadOnlyList<string> runPaths, IReadOnlyList<MergePass> passes)
    {
        List<long> sizes = new(runPaths.Count);
        foreach (string path in runPaths)
        {
            sizes.Add(new FileInfo(path).Length);
        }

        long total = 0;
        foreach (MergePass pass in passes)
        {
            List<long> next = [];
            foreach (int[] group in pass.Groups)
            {
                long groupBytes = 0;
                foreach (int index in group)
                {
                    groupBytes += sizes[index];
                }

                total += groupBytes;
                next.Add(groupBytes);
            }

            foreach (int index in pass.CarriedForward)
            {
                next.Add(sizes[index]);
            }

            sizes = next;
        }

        return total;
    }

    private async Task MergeGroupAsync(
        int[] group, List<string> current, string destination, bool isOutputDestination, CancellationToken ct)
    {
        Stream[] inputs = new Stream[group.Length];
        FileStream? output = null;
        long totalBytes = 0;
        try
        {
            for (int i = 0; i < group.Length; i++)
            {
                // Cursor buffers handle read-ahead; asynchronous sequential reads match run use.
                inputs[i] = new FileStream(
                    current[group[i]], FileMode.Open, FileAccess.Read, FileShare.None, bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                // Runs have one LF terminator per line. A preceding CR is content, so input
                // and output bytes match.
                totalBytes += inputs[i].Length;
            }

            // KWayMerge owns the output buffer, so FileStream uses bufferSize 1. Intermediate
            // private run paths require CreateNew; the final output path may replace a file.
            FileMode mode = isOutputDestination ? FileMode.Create : FileMode.CreateNew;
            output = new FileStream(
                destination, mode, FileAccess.Write, FileShare.None, bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            // The output path is touched as soon as its stream opens.
            if (isOutputDestination)
            {
                OutputOpened = true;
            }

            // Preallocate the exact checked output size to avoid repeated file growth.
            output.SetLength(totalBytes);
        }
        catch
        {
            // Dispose partial opens before KWayMerge takes ownership.
            foreach (Stream? input in inputs)
            {
                input?.Dispose();
            }
            output?.Dispose();
            throw;
        }

        // KWayMerge owns and disposes inputs on success or failure.
        await using (output)
        {
            await KWayMerge.MergeAsync(
                inputs, _cursorBufferSegments[group.Length - 1], output, _outputStagingBuffer, _maxLineLength,
                _progress, ct);

            // A mismatch leaves a zero-filled preallocated tail, so fail rather than return
            // an output whose length and content disagree.
            if (output.Position != totalBytes)
            {
                throw new InvalidOperationException(
                    $"MergeExecutor wrote {output.Position} bytes to '{destination}' but predicted {totalBytes}.");
            }
        }
    }
}
