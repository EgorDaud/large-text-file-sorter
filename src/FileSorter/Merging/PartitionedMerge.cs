using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using FileSorter.Infrastructure;
using FileSorter.LineFormat;
using FileSorter.Planning;

namespace FileSorter.Merging;

internal enum PartitionOutcome
{
    Parallel,
    NoPartition,
    SingleSlice,
}

internal readonly record struct PartitionStats(
    PartitionOutcome Outcome,
    int Workers,
    double SplitterSeconds,
    double Imbalance,
    bool Sparse,
    double QuickestWorkerSeconds,
    double SlowestWorkerSeconds);

internal sealed class PartitionedMerge
{
    private readonly TemporaryRunSet _runs;
    private readonly MemoryPlan _plan;
    private readonly int _maxLineLength;
    private readonly MergeProgress _progress;

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

    // Set before any worker starts, so it is still readable after a worker failure.
    public PartitionStats? Stats { get; private set; }

    public bool OutputOpened { get; private set; }

    // Returns false with outputPath untouched when fewer than two slices hold lines.
    public async Task<bool> TryMergeAsync(IReadOnlyList<string> runPaths, string outputPath, CancellationToken ct)
    {
        int workers = _plan.MergeParallelism;

        Stopwatch splitClock = Stopwatch.StartNew();
        RangePartition? located = RangePartitioner.Locate(runPaths, workers, _maxLineLength, _plan.Parallelism, ct);
        PartitionStats declined = new(PartitionOutcome.NoPartition, 1, splitClock.Elapsed.TotalSeconds, 0, false, 0, 0);

        if (located is not { } partition)
        {
            Stats = declined;
            return false;
        }

        if (partition.NonEmptySlices < 2)
        {
            Stats = declined with { Outcome = PartitionOutcome.SingleSlice, Imbalance = partition.Imbalance };
            return false;
        }

        Stats = declined with { Outcome = PartitionOutcome.Parallel, Workers = workers, Imbalance = partition.Imbalance };

        bool sparse = PreallocateOutput(outputPath, partition.TotalBytes);
        (long[] written, double[] seconds) = await RunWorkersAsync(runPaths, outputPath, partition, ct);
        Stats = Stats.Value with { Sparse = sparse, QuickestWorkerSeconds = seconds.Min(), SlowestWorkerSeconds = seconds.Max() };

        VerifyWrites(written, partition, outputPath);
        FinishOutput(outputPath, sparse, runPaths);
        return true;
    }

    private bool PreallocateOutput(string outputPath, long totalBytes)
    {
        using FileStream preallocate = OutputFile.CreateFresh(outputPath, FileShare.ReadWrite, FileOptions.None);

        OutputOpened = true;

        // Mark sparse before SetLength, or NTFS zero-fills up to each worker's first write.
        bool sparse = SparseFile.TryMarkSparse(preallocate.SafeFileHandle);
        preallocate.SetLength(totalBytes);
        return sparse;
    }

    private async Task<(long[] Written, double[] Seconds)> RunWorkersAsync(
        IReadOnlyList<string> runPaths, string outputPath, RangePartition partition, CancellationToken ct)
    {
        int workers = partition.WorkerCount;

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

        return (written, seconds);
    }

    private static void VerifyWrites(long[] written, RangePartition partition, string outputPath)
    {
        long total = 0;
        for (int w = 0; w < written.Length; w++)
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
    }

    private void FinishOutput(string outputPath, bool sparse, IReadOnlyList<string> runPaths)
    {
        // Best-effort: every byte is written, so failing to clear sparse leaves a correct output.
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
    }

    private async Task<long> MergeSliceAsync(
        IReadOnlyList<string> runPaths, string outputPath, RangePartition partition, int worker, CancellationToken ct)
    {
        long[][] offsets = partition.RunOffsets;
        List<Stream> inputs = new(runPaths.Count);
        List<RunCursorBuffers> buffers = new(runPaths.Count);

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

                if (end <= start)
                {
                    continue;
                }

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

            // Worker 0 reuses the executor's buffer; MemoryPlan budgets one per other worker.
            staging = worker == 0 ? _outputStagingBuffer : new byte[_plan.OutputBufferSize];
        }
        catch
        {
            foreach (Stream input in inputs)
            {
                input.Dispose();
            }

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
                // ByteOffset is slice-relative, and a slice starting mid-run has no line number.
                (string path, long sliceStart) = inputRuns[index];
                throw new MalformedLineException(
                    sliceStart + ex.ByteOffset, MalformedLineException.LineNumberUnavailable, ex.Preview, path);
            }
        }

        return output.BytesWritten;
    }

    // Prefer a real worker failure over the sibling cancellations it triggered, unless the caller cancelled.
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
