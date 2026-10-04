using FileSorter.Infrastructure;
using FileSorter.Planning;

namespace FileSorter.RunGeneration;

internal static class RunGenerationDriver
{
    // Phase-one buffers stay scoped here so they are collectable before the merge allocates.
    public static async Task<IReadOnlyList<string>> GenerateRunsAsync(
        string inputPath,
        int maxLineLength,
        MemoryPlan plan,
        TemporaryRunSet runs,
        RunGenerationStrategy strategy,
        CancellationToken ct)
    {
        using FileStream input = new(
            inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: FileStreams.Unbuffered,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        BufferPool pool = new(plan.ChunkSize, plan.DescriptorCapacity, plan.PoolCapacity);

        // Declared after input so its disposal waits for any in-flight fill before the stream closes.
        await using ChunkReader reader = new(input, pool, maxLineLength);
        ChunkSpiller spiller = new(runs, plan.SpillBufferSize);

        return await strategy(reader, spiller.SpillAsync, plan.Parallelism, ct);
    }
}
