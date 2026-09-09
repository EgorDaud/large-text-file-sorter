using System.Text;
using Akka.Actor;
using Akka.Streams;
using FileSorter.LineFormat;
using FileSorter.RunGeneration;
using FileSorter.Startup;
using Xunit;

namespace FileSorter.Tests.RunGeneration;

// Every test here runs unchanged against both production strategies: the pool is the flow
// control, the exception surfaced is the same unwrapped type, slots are released on the
// success, failure, and cancellation paths, and run order is not defined. A behaviour that
// holds for one scheduler and not the other is a defect, not a library quirk.
public sealed class RunGenerationStrategyTests : IClassFixture<AkkaFixture>
{
    // An upper bound for a saturated thread pool, not an expectation of how long a run
    // takes: it turns a hung strategy into a failure instead of a stalled suite.
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(15);

    private readonly AkkaFixture _akka;

    public RunGenerationStrategyTests(AkkaFixture akka)
    {
        _akka = akka;
    }

    public enum Strategy
    {
        Akka,
        Channels,
    }

    public static TheoryData<Strategy> Strategies => [Strategy.Akka, Strategy.Channels];

    public static TheoryData<Strategy, int> StrategiesAtTwoParallelisms =>
        new()
        {
            { Strategy.Akka, 2 },
            { Strategy.Akka, 4 },
            { Strategy.Channels, 2 },
            { Strategy.Channels, 4 },
        };

    [Theory]
    [MemberData(nameof(StrategiesAtTwoParallelisms))]
    public async Task Outstanding_never_exceeds_pool_capacity_when_the_reader_outpaces_its_spillers(
        Strategy strategy, int parallelism)
    {
        // The pool is the flow control in both strategies, not the channel's own bound
        // and not Akka's internal buffer. A ceiling asserted against either of those
        // would pass even for a strategy with no flow control at all, so this asserts
        // BufferPool.Outstanding directly against PoolCapacity = parallelism + 2: one
        // slot per spiller, plus the chunk being read and the reader's prefetch.
        int poolCapacity = parallelism + 2;
        using MemoryStream input = new(BuildLines(count: 300));
        BufferPool pool = new(bufferSize: 128, descriptorCapacity: 5, capacity: poolCapacity);
        await using ChunkReader reader = new(input, pool, maxLineLength: 32);

        // Slower than the reader by construction: the reader awaits nothing but memory
        // copies and parsing, while every spill sits on a fixed delay, which is what
        // lets chunks pile up against the pool ceiling for the sampler to observe.
        ChunkSpill spill = async (chunk, ct) =>
        {
            await Task.Delay(15, ct);
            chunk.Buffer.Dispose();
            return string.Empty;
        };

        int observedMax = 0;
        using CancellationTokenSource sampling = new();
        Task sampler = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    InterlockedMax(ref observedMax, pool.Outstanding);
                    await Task.Delay(1, sampling.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, TestContext.Current.CancellationToken);

        RunGenerationStrategy run = Resolve(strategy);
        await run(reader, spill, parallelism, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        await sampling.CancelAsync();
        await sampler;

        Assert.True(observedMax <= poolCapacity, $"observed {observedMax} outstanding against a ceiling of {poolCapacity}");
        Assert.True(observedMax > 1, "the scenario never put the pool under real pressure, so the ceiling assertion above proves nothing");
    }

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task A_spill_that_throws_still_releases_the_chunk_it_was_given(Strategy strategy)
    {
        // The release contract is on the delegate, not just on ChunkSpiller: whatever
        // implements ChunkSpill releases the slot it received, on success or on throw.
        // A single-chunk input keeps the count unambiguous — there is no second chunk
        // the reader could have handed off ahead of this one.
        const int parallelism = 2;
        using MemoryStream input = new("1. Apple\n"u8.ToArray());
        BufferPool pool = new(bufferSize: 256, descriptorCapacity: 8, capacity: parallelism + 2);
        await using ChunkReader reader = new(input, pool, maxLineLength: 32);

        ChunkSpill spill = async (chunk, ct) =>
        {
            try
            {
                throw new InvalidOperationException("simulated spill failure");
            }
            finally
            {
                chunk.Buffer.Dispose();
            }
        };

        RunGenerationStrategy run = Resolve(strategy);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => run(reader, spill, parallelism, TestContext.Current.CancellationToken)
                .WaitAsync(BoundedWait, TestContext.Current.CancellationToken));

        Assert.Equal(0, pool.Outstanding);
    }

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task Cancelling_mid_run_releases_the_spilled_chunk_and_the_readers_own_state(Strategy strategy)
    {
        // A chunk the reader has handed off but that never reached spill is not
        // returned on abort. How far a strategy lets the reader run ahead of the one
        // chunk inside spill is its own internal buffering, so this waits for whatever
        // steady state the strategy settles into rather than assuming the ceiling.
        //
        // Two releases are guaranteed on cancellation: spill's own finally, and the
        // reader's slot -- it always holds one between calls, the next chunk's
        // prefetch, which DisposeAsync releases, so every caller must `await using`
        // the reader. Anything a strategy's scheduler buffered beyond that is
        // timing-dependent, which is why only an upper bound is asserted.
        const int parallelism = 1;
        using MemoryStream input = new(BuildLines(count: 50));
        BufferPool pool = new(bufferSize: 128, descriptorCapacity: 2, capacity: parallelism + 2);
        ChunkReader reader = new(input, pool, maxLineLength: 32);
        using CancellationTokenSource cts = new();

        ChunkSpill spill = async (chunk, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
                return string.Empty;
            }
            finally
            {
                chunk.Buffer.Dispose();
            }
        };

        RunGenerationStrategy run = Resolve(strategy);
        Task<IReadOnlyList<string>> runTask = run(reader, spill, parallelism, cts.Token);

        int steadyState = await WaitForStableOutstandingAsync(pool, BoundedWait);
        Assert.True(steadyState >= 1, "spill never received a chunk to hold onto");

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        // Spill's finally has released its chunk whether or not the reader's prefetch
        // was folded into the same release: a cancelled prefetch acquire fails inside
        // ReadNextAsync's catch, which releases the chunk it was mid-delivery of.
        Assert.True(
            pool.Outstanding <= steadyState - 1,
            $"expected at least the spilled chunk's slot to be released (steady state {steadyState}), but {pool.Outstanding} remained outstanding");

        await reader.DisposeAsync();
        Assert.True(
            pool.Outstanding <= steadyState - 1,
            $"disposing the reader must never increase Outstanding (steady state {steadyState}, now {pool.Outstanding})");
    }

    [Fact]
    public async Task Malformed_input_surfaces_the_same_bare_exception_type_from_both_strategies()
    {
        // Parallel.ForEachAsync's fault route is an AggregateException that a plain
        // await already unwraps to the original exception; Akka's materialized Task
        // wraps its stage failure in an AggregateException that survives the await, so
        // AkkaRunGeneration unwraps it at its boundary. The claim is not just that both
        // strategies fail, but that they fail as the identical, unwrapped type.
        Exception akka = await CaptureMalformedInputFailureAsync(Strategy.Akka);
        Exception channels = await CaptureMalformedInputFailureAsync(Strategy.Channels);

        Assert.IsType<MalformedLineException>(akka);
        Assert.Equal(akka.GetType(), channels.GetType());
    }

    [Fact]
    public async Task Cancellation_surfaces_the_same_exception_type_from_both_strategies()
    {
        // An already-cancelled token makes the first call each strategy makes --
        // ChunkReader.ReadNextAsync, shared code neither strategy owns -- the thing
        // that fails, before either scheduler's machinery can diverge from the other's.
        Exception akka = await CaptureCancellationFailureAsync(Strategy.Akka);
        Exception channels = await CaptureCancellationFailureAsync(Strategy.Channels);

        Assert.IsAssignableFrom<OperationCanceledException>(akka);
        Assert.Equal(akka.GetType(), channels.GetType());
    }

    [Fact]
    public async Task Both_strategies_at_two_parallelism_settings_produce_the_same_run_contents()
    {
        // Run order is not defined: the returned list may differ in order and in which
        // chunk lands in which run file across schedulers. Chunk boundaries are fixed
        // by bufferSize and descriptorCapacity alone, so the sorted multiset of run
        // contents must be identical regardless of strategy or parallelism. No
        // assertion here depends on the order of the returned list.
        byte[] data = BuildLines(count: 400);

        List<string> baseline = await RunAndCollectSortedContentsAsync(Strategy.Akka, parallelism: 2, data);
        Assert.Equal(baseline, await RunAndCollectSortedContentsAsync(Strategy.Akka, parallelism: 4, data));
        Assert.Equal(baseline, await RunAndCollectSortedContentsAsync(Strategy.Channels, parallelism: 2, data));
        Assert.Equal(baseline, await RunAndCollectSortedContentsAsync(Strategy.Channels, parallelism: 4, data));
    }

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task A_spill_that_throws_mid_stream_surfaces_its_own_failure_rather_than_hanging(
        Strategy strategy)
    {
        const int parallelism = 2;
        // A disk error partway through a large file, which the single-chunk test cannot
        // reach because there the reader is finished before the spill runs. Here the
        // reader is still going, and a strategy whose reader parks on a full channel
        // nothing will drain again never returns at all: no exception, no exit code,
        // just a stalled sorter.
        using MemoryStream input = new(BuildLines(count: 400));
        BufferPool pool = new(bufferSize: 128, descriptorCapacity: 2, capacity: parallelism + 2);
        await using ChunkReader reader = new(input, pool, maxLineLength: 32);

        int spilled = 0;
        ChunkSpill spill = async (chunk, ct) =>
        {
            try
            {
                await Task.Delay(5, ct);
                if (Interlocked.Increment(ref spilled) == 3)
                {
                    throw new InvalidOperationException("simulated spill failure");
                }

                return string.Empty;
            }
            finally
            {
                chunk.Buffer.Dispose();
            }
        };

        RunGenerationStrategy run = Resolve(strategy);

        // The failure reported is the spill's own, not whatever the reader tripped over
        // once the channel closed under it: that one is a consequence, not the cause.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => run(reader, spill, parallelism, TestContext.Current.CancellationToken)
                .WaitAsync(BoundedWait, TestContext.Current.CancellationToken));
    }

    private async Task<Exception> CaptureMalformedInputFailureAsync(Strategy strategy)
    {
        using MemoryStream input = new("1. Apple\nnotaline\n"u8.ToArray());
        BufferPool pool = new(bufferSize: 256, descriptorCapacity: 8, capacity: 3);
        await using ChunkReader reader = new(input, pool, maxLineLength: 32);
        ChunkSpill spill = (chunk, ct) =>
        {
            chunk.Buffer.Dispose();
            return Task.FromResult(string.Empty);
        };

        return await CaptureExceptionAsync(strategy, reader, spill, parallelism: 2, CancellationToken.None);
    }

    private async Task<Exception> CaptureCancellationFailureAsync(Strategy strategy)
    {
        using MemoryStream input = new(BuildLines(count: 10));
        BufferPool pool = new(bufferSize: 128, descriptorCapacity: 2, capacity: 2);
        await using ChunkReader reader = new(input, pool, maxLineLength: 32);
        ChunkSpill spill = (chunk, ct) =>
        {
            chunk.Buffer.Dispose();
            return Task.FromResult(string.Empty);
        };

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        return await CaptureExceptionAsync(strategy, reader, spill, parallelism: 1, cts.Token);
    }

    private async Task<List<string>> RunAndCollectSortedContentsAsync(Strategy strategy, int parallelism, byte[] data)
    {
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 256, descriptorCapacity: 6, capacity: parallelism + 2);
        await using ChunkReader reader = new(input, pool, maxLineLength: 128);

        string directory = Path.Combine(Path.GetTempPath(), "FileSorterTests", Guid.NewGuid().ToString("N"));
        using TemporaryRunSet runs = new(directory);
        ChunkSpiller spiller = new(runs, spillBufferSize: 4096);

        RunGenerationStrategy run = Resolve(strategy);
        IReadOnlyList<string> paths = await run(reader, spiller.SpillAsync, parallelism, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        List<string> contents = [];
        foreach (string path in paths)
        {
            contents.Add(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }

        Directory.Delete(directory, recursive: true);
        contents.Sort(StringComparer.Ordinal);
        return contents;
    }

    private async Task<Exception> CaptureExceptionAsync(
        Strategy strategy, ChunkReader reader, ChunkSpill spill, int parallelism, CancellationToken ct)
    {
        RunGenerationStrategy run = Resolve(strategy);
        try
        {
            await run(reader, spill, parallelism, ct);
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException($"{strategy} was expected to fail but completed successfully.");
    }

    private RunGenerationStrategy Resolve(Strategy strategy) => strategy switch
    {
        Strategy.Akka => (reader, spill, parallelism, ct) =>
            AkkaRunGeneration.RunAsync(reader, spill, parallelism, _akka.Materializer, ct),
        Strategy.Channels => ChannelRunGeneration.RunAsync,
        _ => throw new ArgumentOutOfRangeException(nameof(strategy)),
    };

    private static byte[] BuildLines(int count) =>
        Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, count).Select(i => $"{i}. Line number {i}\n")));

    private static void InterlockedMax(ref int target, int candidate)
    {
        int prior;
        do
        {
            prior = Volatile.Read(ref target);
            if (candidate <= prior)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, candidate, prior) != prior);
    }

    // Polls Outstanding until it has not moved for twenty consecutive samples, then
    // returns that value: how long a strategy takes to reach steady state, and what
    // that steady state is, are both strategy-dependent, so neither a fixed delay nor
    // a target value would work here.
    private static async Task<int> WaitForStableOutstandingAsync(BufferPool pool, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        int last = pool.Outstanding;
        int stableStreak = 0;
        while (stableStreak < 20)
        {
            await Task.Delay(5);
            int current = pool.Outstanding;
            if (current == last)
            {
                stableStreak++;
            }
            else
            {
                stableStreak = 0;
                last = current;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Outstanding never stabilised within {timeout} (last observed {last}).");
            }
        }

        return last;
    }
}

// One ActorSystem shared across every test in the class: starting one is expensive and
// nothing under test depends on system identity.
public sealed class AkkaFixture : IAsyncLifetime
{
    private ActorSystem? _system;

    public IMaterializer Materializer { get; private set; } = null!;

    public ValueTask InitializeAsync()
    {
        _system = ActorSystem.Create(nameof(AkkaFixture));
        Materializer = _system.Materializer();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_system is not null)
        {
            await _system.Terminate();
        }
    }
}
