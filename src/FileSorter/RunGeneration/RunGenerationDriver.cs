using System.Diagnostics;
using FileSorter.Infrastructure;
using FileSorter.Planning;
using Shared;

namespace FileSorter.RunGeneration;

internal static class RunGenerationDriver
{
    // Phase-one buffers stay scoped here so they are collectable before the merge allocates.
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

        // Declared after input so its disposal waits for any in-flight fill before the stream closes.
        await using ChunkReader reader = new(input, pool, maxLineLength);
        ChunkSpiller spiller = new(runs, plan.SpillBufferSize);

        return await ProgressReporter.RunWithProgressAsync(
            progressCt => ReportReadProgressAsync(reader, inputInfo.Length, clock, progressCt),
            () => strategy(reader, spiller.SpillAsync, plan.Parallelism, ct),
            ct);
    }

    // Reads BytesConsumed without locking; aligned long reads are atomic on 64-bit targets.
    private static Task ReportReadProgressAsync(
        ChunkReader reader, long inputSizeBytes, Stopwatch clock, CancellationToken ct)
    {
        string ofTotal = inputSizeBytes > 0 ? $" of {ByteSize.Describe(inputSizeBytes)}" : string.Empty;
        return ProgressReporter.TickAsync(
            () => $"  read {ByteSize.Describe(reader.BytesConsumed)}{ofTotal} ({clock.Elapsed.TotalSeconds:F1}s)",
            ct);
    }
}
