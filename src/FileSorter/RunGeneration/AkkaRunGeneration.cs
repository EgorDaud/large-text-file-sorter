using System.Runtime.ExceptionServices;
using Akka;
using Akka.Actor;
using Akka.Configuration;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Util;

namespace FileSorter.RunGeneration;

internal static class AkkaRunGeneration
{
    public static ActorSystem CreateQuietSystem(string name) =>
        ActorSystem.Create(name, ConfigurationFactory.ParseString("akka.loglevel = OFF\nakka.stdout-loglevel = OFF"));

    public static RunGenerationStrategy Strategy(IMaterializer materializer) =>
        (reader, spill, parallelism, ct) => RunAsync(reader, spill, parallelism, materializer, ct);

    public static async Task<IReadOnlyList<string>> RunAsync(
        ChunkReader reader, ChunkSpill spill, int parallelism, IMaterializer materializer, CancellationToken ct)
    {
        // The graph can finish while its reads and spills still run; join them before the
        // caller disposes the reader, pool, and run registry.
        using CancellationTokenSource abort = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // UnfoldAsync never overlaps reads, so this is the only outstanding one.
        Task? reading = null;

        SpillCount spills = new();

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

        // Task.Run keeps the synchronous sort off the actor thread. It must start even after
        // cancellation (CancellationToken.None) so SpillAsync releases the pool slot.
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
            // RunWith wraps stage failures; unwrap to match the other strategy's failure contract.
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            throw;
        }
        finally
        {
            await DrainAsync();
        }
    }

    // The idle latch is created only after the graph stops: while it runs, zero can be a gap between chunks.
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
