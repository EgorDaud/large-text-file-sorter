using System.Text;
using Akka.Actor;
using Akka.Streams;
using FileSorter.Infrastructure;
using FileSorter.LineFormat;
using FileSorter.RunGeneration;
using FileSorter.Tests.Support;
using Xunit;
using static FileSorter.Tests.Support.TestTimeouts;

namespace FileSorter.Tests.RunGeneration;

public sealed class RunGenerationStrategyTests : IClassFixture<AkkaFixture>
{
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
    public async Task Exactly_parallelism_spills_run_at_once_when_the_reader_outpaces_its_spillers(
        Strategy strategy, int parallelism)
    {
        // The pool leaves one chunk beyond the spillers, so a strategy that ignores parallelism overshoots by one.
        using MemoryStream input = new(BuildLines(count: 300));
        BufferPool pool = new(bufferSize: 128, descriptorCapacity: 5, capacity: parallelism + 2);
        await using ChunkReader reader = new(input, pool, maxLineLength: 32);

        TaskCompletionSource saturated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0;
        int peak = 0;
        ChunkSpill spill = async (chunk, ct) =>
        {
            int now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            try
            {
                if (now == parallelism)
                {
                    saturated.TrySetResult();
                }

                await release.Task.WaitAsync(ct);
                return string.Empty;
            }
            finally
            {
                Interlocked.Decrement(ref running);
                chunk.Buffer.Dispose();
            }
        };

        RunGenerationStrategy run = Resolve(strategy);
        Task<IReadOnlyList<string>> runTask = run(reader, spill, parallelism, TestContext.Current.CancellationToken);

        await saturated.Task.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        await WaitForStableOutstandingAsync(pool, BoundedWait);
        release.SetResult();
        await runTask.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(parallelism, peak);
    }

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task A_spill_that_throws_still_releases_the_chunk_it_was_given(Strategy strategy)
    {
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
        // Only an upper bound: what a scheduler buffers beyond the spilled chunk and the prefetch is timing-dependent.
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
        Exception akka = await CaptureMalformedInputFailureAsync(Strategy.Akka);
        Exception channels = await CaptureMalformedInputFailureAsync(Strategy.Channels);

        Assert.IsType<MalformedLineException>(akka);
        Assert.Equal(akka.GetType(), channels.GetType());
    }

    [Fact]
    public async Task Cancellation_surfaces_the_same_exception_type_from_both_strategies()
    {
        Exception akka = await CaptureCancellationFailureAsync(Strategy.Akka);
        Exception channels = await CaptureCancellationFailureAsync(Strategy.Channels);

        Assert.IsAssignableFrom<OperationCanceledException>(akka);
        Assert.Equal(akka.GetType(), channels.GetType());
    }

    [Fact]
    public async Task Both_strategies_at_two_parallelism_settings_produce_the_same_run_contents()
    {
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

        using TempDirectory directory = new();
        using TemporaryRunSet runs = new(directory.Path);
        ChunkSpiller spiller = new(runs, spillBufferSize: 4096);

        RunGenerationStrategy run = Resolve(strategy);
        IReadOnlyList<string> paths = await run(reader, spiller.SpillAsync, parallelism, TestContext.Current.CancellationToken)
            .WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        List<string> contents = [];
        foreach (string path in paths)
        {
            contents.Add(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }

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
        Strategy.Akka => AkkaRunGeneration.Strategy(_akka.Materializer),
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

public sealed class AkkaFixture : IAsyncLifetime
{
    private ActorSystem? _system;

    public IMaterializer Materializer { get; private set; } = null!;

    public ValueTask InitializeAsync()
    {
        _system = AkkaRunGeneration.CreateQuietSystem(nameof(AkkaFixture));
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
