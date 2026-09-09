using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace FileSorter.RunGeneration;

internal static class ChannelRunGeneration
{
    public static async Task<IReadOnlyList<string>> RunAsync(
        ChunkReader reader, ChunkSpill spill, int parallelism, CancellationToken ct)
    {
        Channel<Chunk> channel = Channel.CreateBounded<Chunk>(parallelism);
        ConcurrentBag<string> paths = [];

        Task produce = ProduceAsync(reader, channel.Writer, ct);
        Task consume = Parallel.ForEachAsync(channel.Reader.ReadAllAsync(ct), new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = ct,
        }, async (chunk, token) => paths.Add(await spill(chunk, token)));

        // A failed consumer stops draining the bounded channel. Complete it so a blocked
        // producer wakes, releases its resources, and propagates the failure.
        _ = consume.ContinueWith(
            static (faulted, state) => ((Channel<Chunk>)state!).Writer.TryComplete(faulted.Exception),
            channel,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            // WhenAll reports by task order, so RootCause restores a consumer failure
            // hidden by the producer's ChannelClosedException.
            await Task.WhenAll(produce, consume);
        }
        catch (Exception ex)
        {
            ExceptionDispatchInfo.Capture(RootCause(ex, consume)).Throw();
        }

        return paths.ToArray();
    }

    // Prefer the consumer failure when it caused the producer's channel to close.
    private static Exception RootCause(Exception surfaced, Task consume)
    {
        // This ChannelClosedException is a consequence of the continuation above;
        // the consumer has the original failure.
        if (surfaced is ChannelClosedException && consume.Exception?.Flatten().InnerExceptions[0] is { } consumerFailure)
        {
            return consumerFailure;
        }

        return surfaced;
    }

    private static async Task ProduceAsync(ChunkReader reader, ChannelWriter<Chunk> writer, CancellationToken ct)
    {
        Exception? failure = null;
        try
        {
            while (await reader.ReadNextAsync(ct) is { } chunk)
            {
                try
                {
                    await writer.WriteAsync(chunk, ct);
                }
                catch
                {
                    // This chunk never reached a consumer, so return its pool slot.
                    chunk.Buffer.Dispose();
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            // ReadAllAsync needs completion on both success and failure.
            writer.TryComplete(failure);
        }
    }
}
