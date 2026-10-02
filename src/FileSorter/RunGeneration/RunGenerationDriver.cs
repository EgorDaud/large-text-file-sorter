using System.Diagnostics;
using FileSorter.Infrastructure;
using FileSorter.Planning;

namespace FileSorter.RunGeneration;

// Creates phase-one resources, reports read progress, and returns generated run paths.
internal static class RunGenerationDriver
{
    // Keep phase-one allocations scoped here so they can be collected before the merge
    // allocates its own budgeted buffers.
    internal static async Task<IReadOnlyList<string>> GenerateRunsAsync(
        FileInfo inputInfo,
        int maxLineLength,
        MemoryPlan plan,
        TemporaryRunSet runs,
        RunGenerationStrategy strategy,
        Stopwatch clock,
        CancellationToken ct)
    {
        using FileStream input = new(
            inputInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: FileStreams.Unbuffered,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        BufferPool pool = new(plan.ChunkSize, plan.DescriptorCapacity, plan.PoolCapacity);

        // Declared after input so asynchronous disposal waits for any fill before the
        // stream closes.
        await using ChunkReader reader = new(input, pool, maxLineLength);
        ChunkSpiller spiller = new(runs, plan.SpillBufferSize);

        return await ProgressReporter.RunWithProgressAsync(
            progressCt => ReportReadProgressAsync(reader, inputInfo.Length, clock, progressCt),
            () => strategy(reader, spiller.SpillAsync, plan.Parallelism, ct),
            ct);
    }

    // Phase one exposes only BytesConsumed. Supported 64-bit targets read its aligned
    // long atomically, so reporting can read it directly.
    private static async Task ReportReadProgressAsync(
        ChunkReader reader, long inputSizeBytes, Stopwatch clock, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(ProgressReporter.IntervalMilliseconds, ct);

                long consumed = reader.BytesConsumed;
                string ofTotal = inputSizeBytes > 0 ? $" of {ProgressReporter.Describe(inputSizeBytes)}" : string.Empty;
                Console.Error.WriteLine($"  read {ProgressReporter.Describe(consumed)}{ofTotal} ({clock.Elapsed.TotalSeconds:F1}s)");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
