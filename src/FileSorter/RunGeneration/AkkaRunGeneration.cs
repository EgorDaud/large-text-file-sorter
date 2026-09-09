using System.Runtime.ExceptionServices;
using Akka;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Util;

namespace FileSorter.RunGeneration;

internal static class AkkaRunGeneration
{
    public static async Task<IReadOnlyList<string>> RunAsync(
        ChunkReader reader, ChunkSpill spill, int parallelism, IMaterializer materializer, CancellationToken ct)
    {
        // The graph can finish while reads and spills it started are still running.
        // Cancel and join that work before returning because the caller then disposes
        // the reader, pool, and temporary-run registry. ChunkReader owns any look-ahead fill.
        using CancellationTokenSource abort = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // UnfoldAsync awaits one read before starting the next, so this tracks the only
        // outstanding ReadNextAsync task without retaining a task per chunk.
        Task? reading = null;

        // Spills run concurrently. Count them so tracking stays bounded by parallelism.
        SpillCount spills = new();

        // The reader keeps its position, so NotUsed is only UnfoldAsync state. A null
        // chunk becomes Option.None and completes the source.
        async Task<Option<(NotUsed, Chunk)>> ReadNextAsync(NotUsed state)
        {
            Chunk? chunk = await reader.ReadNextAsync(abort.Token);
            return chunk is null ? Option<(NotUsed, Chunk)>.None : (state, chunk.Value);
        }

        Task<Option<(NotUsed, Chunk)>> StartRead(NotUsed state)
        {
            Task<Option<(NotUsed, Chunk)>> pending = ReadNextAsync(state);
            reading = pending;
            return pending;
        }

        // Move synchronous sorting and file setup off the fused stage's actor thread.
        // Task.Run must start even after cancellation so SpillAsync can release the pool slot;
        // SpillAsync itself observes abort. Count the spill before starting it and until it ends.
        async Task<string> StartSpill(Chunk chunk)
        {
            spills.Enter();
            try
            {
                return await Task.Run(() => spill(chunk, abort.Token), CancellationToken.None);
            }
            finally
            {
                spills.Leave();
            }
        }

        // Stop and join work started by the graph. Ignore a read failure here because
        // the graph failure, or its cancellation, is already being propagated.
        async Task DrainAsync()
        {
            await abort.CancelAsync();

            if (reading is not null)
            {
                try
                {
                    await reading;
                }
                catch (Exception)
                {
                }
            }

            await spills.WaitForIdleAsync();
        }

        try
        {
            return await Source.UnfoldAsync(NotUsed.Instance, StartRead)
                .SelectAsyncUnordered(parallelism, StartSpill)
                .RunWith(Sink.Seq<string>(), materializer);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            // RunWith preserves a stage failure in AggregateException. Expose the inner
            // exception so both run-generation strategies have the same failure contract.
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            throw;
        }
        finally
        {
            await DrainAsync();
        }
    }

    // Tracks active spills without retaining completed tasks. The idle latch is created
    // after the graph stops because zero can be a gap between chunks while it is running.
    private sealed class SpillCount
    {
        private readonly Lock _gate = new();
        private int _running;
        private TaskCompletionSource? _idle;

        public void Enter()
        {
            lock (_gate)
            {
                _running++;
            }
        }

        public void Leave()
        {
            lock (_gate)
            {
                if (--_running == 0)
                {
                    _idle?.TrySetResult();
                }
            }
        }

        // Called after the graph stops, when the mapper can no longer add spills.
        public Task WaitForIdleAsync()
        {
            lock (_gate)
            {
                _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_running == 0)
                {
                    _idle.TrySetResult();
                }

                return _idle.Task;
            }
        }
    }
}
