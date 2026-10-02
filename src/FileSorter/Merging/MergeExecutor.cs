using FileSorter.Infrastructure;
using FileSorter.LineFormat;
using FileSorter.Planning;
using Shared;

namespace FileSorter.Merging;

internal sealed class MergeExecutor
{
    private readonly TemporaryRunSet _runs;
    private readonly MemoryPlan _plan;
    private readonly int _maxLineLength;

    private RunCursorBuffers[] _cursorBuffers = [];

    private readonly byte[] _outputStagingBuffer;

    private readonly MergeProgress _progress = new();

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

    public int PlannedPasses { get; private set; }

    public long BytesWritten => _progress.BytesWritten;

    public long TotalBytesToWrite { get; private set; }

    public double OutputWaitSeconds => _progress.OutputWaitSeconds;

    public PartitionStats? Partition => _partitioned.Stats;

    public async Task ExecuteAsync(IReadOnlyList<string> runPaths, string outputPath, CancellationToken ct)
    {
        if (runPaths.Count < 2)
        {
            throw new ArgumentException(
                $"MergeExecutor merges two or more runs; a single run is moved into place by Program without a merge. Got {runPaths.Count}.",
                nameof(runPaths));
        }

        try
        {
            await MergeAsync(runPaths, outputPath, ct);
        }
        catch
        {
            if (_sequentialOutputOpened || _partitioned.OutputOpened)
            {
                StagingFile.Delete(outputPath);
            }

            throw;
        }
    }

    private async Task MergeAsync(IReadOnlyList<string> runPaths, string outputPath, CancellationToken ct)
    {
        IReadOnlyList<MergePass> passes = MergePlanner.Plan(runPaths.Count, _plan.MergeFanIn);
        PlannedPasses = passes.Count;
        TotalBytesToWrite = ComputeTotalBytesToWrite(runPaths, passes);

        if (_plan.MergeParallelism > 1 && IsOnePassOverEveryRun(passes)
            && await _partitioned.TryMergeAsync(runPaths, outputPath, ct))
        {
            PassesExecuted = 1;
            return;
        }

        int fanIn = Math.Min(runPaths.Count, _plan.MergeFanIn);

        _cursorBuffers = new RunCursorBuffers[fanIn];
        for (int i = 0; i < fanIn; i++)
        {
            _cursorBuffers[i] = RunCursorBuffers.Create(_plan);
        }

        List<string> current = [.. runPaths];

        for (int passIndex = 0; passIndex < passes.Count; passIndex++)
        {
            MergePass pass = passes[passIndex];

            bool finalPass = passIndex == passes.Count - 1;
            List<string> next = [];

            foreach (int[] group in pass.Groups)
            {
                string destination = finalPass ? outputPath : _runs.CreateRunPath();
                await MergeGroupAsync(group, current, destination, isOutputDestination: finalPass, ct);

                // Delete promptly to keep temporary disk usage near input size.
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

    private static bool IsOnePassOverEveryRun(IReadOnlyList<MergePass> passes) =>
        passes.Count == 1 && passes[0].Groups.Count == 1 && passes[0].CarriedForward.Count == 0;

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
                inputs[i] = new FileStream(
                    current[group[i]], FileMode.Open, FileAccess.Read, FileShare.None, bufferSize: FileStreams.Unbuffered,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                // A merge copies every line byte for byte, so output length equals summed input length.
                totalBytes += inputs[i].Length;
            }

            const FileOptions outputOptions = FileOptions.Asynchronous | FileOptions.SequentialScan;
            output = isOutputDestination
                ? OutputFile.CreateFresh(destination, FileShare.None, outputOptions)
                : new FileStream(
                    destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: FileStreams.Unbuffered,
                    outputOptions);

            if (isOutputDestination)
            {
                _sequentialOutputOpened = true;
            }

            output.SetLength(totalBytes);
        }
        catch
        {
            foreach (Stream? input in inputs)
            {
                input?.Dispose();
            }
            output?.Dispose();
            throw;
        }

        await using (output)
        {
            try
            {
                await KWayMerge.MergeAsync(
                    inputs, _cursorBuffers, output, _outputStagingBuffer, _maxLineLength,
                    _progress, ct);
            }
            catch (MalformedLineException ex) when (ex.RunIndex is { } index && index < group.Length)
            {
                throw new MalformedLineException(ex.ByteOffset, ex.LineNumber, ex.Preview, current[group[index]]);
            }

            // A short write would leave a zero-filled preallocated tail.
            if (output.Position != totalBytes)
            {
                throw new InvalidOperationException(
                    $"MergeExecutor wrote {output.Position} bytes to '{destination}' but predicted {totalBytes}.");
            }
        }
    }
}
