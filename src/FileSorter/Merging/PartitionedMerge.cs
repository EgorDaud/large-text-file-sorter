using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using FileSorter.Infrastructure;
using FileSorter.LineFormat;
using FileSorter.Planning;

namespace FileSorter.Merging;

// What a partitioned attempt reports. Workers is the merge parallelism actually used, or 1
// when the attempt fell back to the sequential path; Imbalance is then infinity for a
// partition with an empty slice, or 0 when no partition could be sampled. The worker times
// are zero unless workers ran. Their gap can reveal device stragglers beyond byte imbalance.
internal readonly record struct PartitionStats(
    int Workers,
    double SplitterSeconds,
    double Imbalance,               // largest worker slice divided by the smallest
    bool Sparse,                    // output marked sparse before SetLength, else plain
    double QuickestWorkerSeconds,
    double SlowestWorkerSeconds);

// The partitioned merge path: locates key ranges and merges them independently into disjoint
// preallocated output ranges. MergeExecutor decides whether to try it.
internal sealed class PartitionedMerge
{
    private readonly TemporaryRunSet _runs;
    private readonly MemoryPlan _plan;
    private readonly int _maxLineLength;
    private readonly MergeProgress _progress;

    // Owned by MergeExecutor; worker 0 reuses it as its output staging buffer.
    private readonly byte[] _outputStagingBuffer;

    public PartitionedMerge(
        TemporaryRunSet runs, MemoryPlan plan, int maxLineLength, MergeProgress progress, byte[] outputStagingBuffer)
    {
        _runs = runs;
        _plan = plan;
        _maxLineLength = maxLineLength;
        _progress = progress;
        _outputStagingBuffer = outputStagingBuffer;
    }

    // Null until an attempt has sampled. It is set before any worker starts, so it is still
    // readable after a worker failure.
    public PartitionStats? Stats { get; private set; }

    // True once outputPath is preallocated, allowing callers to decide whether failure
    // cleanup should delete it.
    public bool OutputOpened { get; private set; }

    // Locates key ranges and merges them independently into disjoint preallocated output
    // ranges. Returns false without touching outputPath when no usable partition exists:
    // none could be sampled, or a slice is empty so one worker would merge nearly
    // everything (all-equal keys). The caller then merges sequentially.
    public async Task<bool> TryMergeAsync(IReadOnlyList<string> runPaths, string outputPath, CancellationToken ct)
    {
        int workers = _plan.MergeParallelism;

        Stopwatch splitClock = Stopwatch.StartNew();
        RangePartition? located = RangePartitioner.Locate(runPaths, workers, _maxLineLength, _plan.Parallelism, ct);
        splitClock.Stop();
        double splitterSeconds = splitClock.Elapsed.TotalSeconds;

        if (located is not { } partition)
        {
            Stats = new PartitionStats(1, splitterSeconds, 0, false, 0, 0);
            return false;
        }

        // Repeated splitters leave workers 0 to N-2 an empty slice and the last worker
        // nearly everything, with a window sized for N workers. Decide before the output is
        // preallocated, so the sequential path starts from an untouched output.
        double imbalance = partition.Imbalance;
        if (double.IsPositiveInfinity(imbalance))
        {
            Stats = new PartitionStats(1, splitterSeconds, imbalance, false, 0, 0);
            return false;
        }

        Stats = new PartitionStats(workers, splitterSeconds, imbalance, false, 0, 0);

        // Preallocate once so worker offsets are stable and no worker extends the file.
        bool sparse;
        using (FileStream preallocate = new(
                   outputPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, bufferSize: FileStreams.Unbuffered))
        {
            // The output path is now touched even if later setup fails.
            OutputOpened = true;

            // Mark sparse before SetLength so nonzero-offset workers create holes instead
            // of forcing NTFS to zero-fill up to their first write. Failure is optional and
            // leaves the ordinary preallocated-file behavior.
            sparse = SparseFile.TryMarkSparse(preallocate.SafeFileHandle);
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

        Stats = new PartitionStats(workers, splitterSeconds, imbalance, sparse, seconds.Min(), seconds.Max());

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
        if (sparse)
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
                    runPaths[r], FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: FileStreams.Unbuffered,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                inputs.Add(new RunSliceStream(file, start, end));
                buffers.Add(RunCursorBuffers.Create(_plan));
                inputRuns.Add((runPaths[r], start));
            }

            long sliceStart = partition.OutputOffsets[worker];
            destination = new FileStream(
                outputPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: FileStreams.Unbuffered,
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
}
