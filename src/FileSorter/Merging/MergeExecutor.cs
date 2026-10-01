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

    private readonly PartitionedMerge _partitioned;
    private bool _sequentialOutputOpened;

    public MergeExecutor(TemporaryRunSet runs, MemoryPlan plan, int maxLineLength)
    {
        _runs = runs;
        _plan = plan;
        _maxLineLength = maxLineLength;
        _outputStagingBuffer = new byte[plan.OutputBufferSize];
        _partitioned = new PartitionedMerge(runs, plan, maxLineLength, _progress, _outputStagingBuffer);
    }

    public int PassesExecuted { get; private set; }

    // Planned pass count; zero before ExecuteAsync.
    public int PlannedPasses { get; private set; }

    // True once outputPath is opened, allowing callers to decide whether failure cleanup
    // should delete it.
    public bool OutputOpened => _sequentialOutputOpened || _partitioned.OutputOpened;

    // Bytes written across all groups and passes; safe for concurrent progress reads.
    public long BytesWritten => _progress.BytesWritten;

    // Exact bytes across all planned passes; zero before ExecuteAsync.
    public long TotalBytesToWrite { get; private set; }

    // Output-write wait time summed across all workers and passes.
    public double OutputWaitSeconds => _progress.OutputWaitSeconds;

    // Stats of the partitioned attempt; null when no attempt was made (sequential plan or
    // multi-pass merge). It reports Workers of 1 when an attempt fell back to sequential.
    public PartitionStats? Partition => _partitioned.Stats;

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

        // Partition only a one-pass merge over every run. Multi-pass partitioning requires
        // intermediate runs that do not yet exist. A declined attempt (no usable partition,
        // COR-8) keeps the plan's per-worker window, as the multi-pass path does: windows
        // cap at 4 MiB, so enlarging them gains little and would need the bounds re-derived.
        if (_plan.MergeParallelism > 1 && IsOnePassOverEveryRun(passes)
            && await _partitioned.TryMergeAsync(runPaths, outputPath, ct))
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
                _sequentialOutputOpened = true;
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
