using System.Diagnostics;
using FileSorter.Startup;

namespace FileSorter.RunGeneration;

// Creates phase-one resources, reports read progress, and returns generated run paths.
internal static class RunGenerationDriver
{
    // Keep phase-one allocations scoped here so they can be collected before the merge
    // allocates its own budgeted buffers.
    internal static async Task<IReadOnlyList<string>> GenerateRunsAsync(
        SorterOptions options,
        FileInfo inputInfo,
        MemoryPlan plan,
        TemporaryRunSet runs,
        RunGenerationStrategy strategy,
        Stopwatch clock,
        CancellationToken ct)
    {
        using FileStream input = new(
            options.InputPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        BufferPool pool = new(plan.ChunkSize, plan.DescriptorCapacity, plan.PoolCapacity);

        // Declared after input so asynchronous disposal waits for any fill before the
        // stream closes.
        await using ChunkReader reader = new(input, pool, options.MaxLineLength);
        ChunkSpiller spiller = new(runs, plan.SpillBufferSize);

        return await RunPhaseOneAsync(
            reader, strategy, spiller.SpillAsync, plan.Parallelism, inputInfo.Length, clock, ct);
    }

    private static async Task<IReadOnlyList<string>> RunPhaseOneAsync(
        ChunkReader reader,
        RunGenerationStrategy strategy,
        ChunkSpill spill,
        int parallelism,
        long inputSizeBytes,
        Stopwatch clock,
        CancellationToken ct)
    {
        using CancellationTokenSource progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task progressTask = ProgressReporter.ReportReadProgressAsync(reader, inputSizeBytes, clock, progressCts.Token);
        try
        {
            return await strategy(reader, spill, parallelism, ct);
        }
        finally
        {
            progressCts.Cancel();
            try
            {
                await progressTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
