using System.Diagnostics;
using System.Text;
using FileSorter.LineFormat;
using FileSorter.RunGeneration;
using Xunit;
using static FileSorter.Tests.Support.TestTimeouts;

namespace FileSorter.Tests.RunGeneration;

public sealed class BackpressureAndCancellationTests : IClassFixture<AkkaFixture>
{
    // Stays under BoundedWait so a cancellation that never unwinds fails here, not at the outer timeout.
    private static readonly TimeSpan CancellationResponsivenessBound = TimeSpan.FromSeconds(10);

    // Not a pass-side timing assumption: a joining strategy cannot finish while the gate is held.
    private static readonly TimeSpan UnjoinedReturnWindow = TimeSpan.FromMilliseconds(250);

    private readonly AkkaFixture _akka;

    public BackpressureAndCancellationTests(AkkaFixture akka)
    {
        _akka = akka;
    }

    [Theory]
    [MemberData(nameof(RunGenerationStrategyTests.Strategies), MemberType = typeof(RunGenerationStrategyTests))]
    [Trait("Case", "SL-01")]
    public async Task A_held_open_spill_stalls_the_reader_until_a_slot_is_released(
        RunGenerationStrategyTests.Strategy strategy)
    {
        const int parallelism = 1;
        const int totalLines = 300;
        int poolCapacity = parallelism + 2;
        using MemoryStream input = new(BuildLines(totalLines));
        BufferPool pool = new(bufferSize: 128, descriptorCapacity: 2, capacity: poolCapacity);
        await using ChunkReader reader = new(input, pool, maxLineLength: 32);

        TaskCompletionSource heldOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int firstSpillClaimed = 0;
        ChunkSpill spill = async (chunk, ct) =>
        {
            try
            {
                if (Interlocked.Exchange(ref firstSpillClaimed, 1) == 0)
                {
                    await heldOpen.Task.WaitAsync(ct);
                }

                return string.Empty;
            }
            finally
            {
                chunk.Buffer.Dispose();
            }
        };

        RunGenerationStrategy run = Resolve(strategy);
        Task<IReadOnlyList<string>> runTask = run(reader, spill, parallelism, TestContext.Current.CancellationToken);

        long stalledAt = await WaitForStableLinesReadAsync(reader, BoundedWait);
        Assert.True(
            stalledAt < totalLines,
            "the reader finished the whole stream before the pool ever filled, so this proves nothing about a stall");

        // Channels fills the pool; Akka at parallelism 1 stalls holding only the blocked chunk.
        Assert.InRange(pool.Outstanding, 1, poolCapacity);

        heldOpen.SetResult();

        await WaitForLinesReadBeyondAsync(reader, stalledAt, BoundedWait);
        IReadOnlyList<string> paths = await runTask.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.Equal(totalLines, reader.LinesRead);
        Assert.NotEmpty(paths);
    }

    [Theory]
    [MemberData(nameof(RunGenerationStrategyTests.Strategies), MemberType = typeof(RunGenerationStrategyTests))]
    [Trait("Case", "SL-02")]
    public async Task Cancelling_mid_run_unwinds_well_before_the_input_would_have_drained(
        RunGenerationStrategyTests.Strategy strategy)
    {
        const int parallelism = 2;
        const int totalLines = 1000;

        // Uncancelled drain: (1000 / 2) chunks * 50 ms / 2 workers ~ 12.5 s.
        using MemoryStream input = new(BuildLines(totalLines));
        BufferPool pool = new(bufferSize: 128, descriptorCapacity: 2, capacity: parallelism + 2);
        await using ChunkReader reader = new(input, pool, maxLineLength: 32);

        TaskCompletionSource firstSpillStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        ChunkSpill spill = async (chunk, ct) =>
        {
            try
            {
                firstSpillStarted.TrySetResult();
                await Task.Delay(50, ct);
                return string.Empty;
            }
            finally
            {
                chunk.Buffer.Dispose();
            }
        };

        RunGenerationStrategy run = Resolve(strategy);
        using CancellationTokenSource cts = new();
        Task<IReadOnlyList<string>> runTask = run(reader, spill, parallelism, cts.Token);

        await firstSpillStarted.Task.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Stopwatch clock = Stopwatch.StartNew();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runTask.WaitAsync(BoundedWait, TestContext.Current.CancellationToken));
        clock.Stop();

        Assert.True(
            clock.Elapsed < CancellationResponsivenessBound,
            $"Cancellation took {clock.Elapsed}, which is not under the stated {CancellationResponsivenessBound} bound.");
    }

    [Theory]
    [MemberData(nameof(RunGenerationStrategyTests.Strategies), MemberType = typeof(RunGenerationStrategyTests))]
    [Trait("Case", "SL-03")]
    public async Task A_spill_failure_does_not_end_the_run_while_a_sibling_spill_is_still_running(
        RunGenerationStrategyTests.Strategy strategy)
    {
        const int parallelism = 2;
        using MemoryStream input = new(BuildLines(count: 20));
        BufferPool pool = new(bufferSize: 128, descriptorCapacity: 2, capacity: parallelism + 2);
        await using ChunkReader reader = new(input, pool, maxLineLength: 32);
        RunRegistry registry = new();

        TaskCompletionSource siblingSpilling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource failureRaised = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int started = 0;
        ChunkSpill spill = async (chunk, ct) =>
        {
            try
            {
                if (Interlocked.Increment(ref started) == 1)
                {
                    siblingSpilling.SetResult();

                    // Not ct: a spill that abandons on cancellation would join trivially.
                    await release.Task;
                    return registry.CreateRunPath();
                }

                await siblingSpilling.Task.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
                failureRaised.TrySetResult();
                throw new IOException("simulated spill failure");
            }
            finally
            {
                chunk.Buffer.Dispose();
            }
        };

        RunGenerationStrategy run = Resolve(strategy);
        Task<IReadOnlyList<string>> runTask = run(reader, spill, parallelism, TestContext.Current.CancellationToken);

        await failureRaised.Task.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Exception failure = await ReleaseAndCaptureAsync(runTask, release, registry);
        Assert.IsType<IOException>(failure);
        Assert.Equal("simulated spill failure", failure.Message);
    }

    [Theory]
    [MemberData(nameof(RunGenerationStrategyTests.Strategies), MemberType = typeof(RunGenerationStrategyTests))]
    [Trait("Case", "SL-04")]
    public async Task A_malformed_line_does_not_end_the_run_while_a_spill_is_still_running(
        RunGenerationStrategyTests.Strategy strategy)
    {
        const int parallelism = 2;
        const int linesPerChunk = 2;
        using MemoryStream input = new("1. Apple\n2. Banana\nnotaline\n"u8.ToArray());
        BufferPool pool = new(bufferSize: 128, descriptorCapacity: linesPerChunk, capacity: parallelism + 2);
        await using ChunkReader reader = new(input, pool, maxLineLength: 32);
        RunRegistry registry = new();

        TaskCompletionSource spilling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        RunGenerationStrategy run = Resolve(strategy);
        Task<IReadOnlyList<string>> runTask =
            run(reader, HeldSpill(spilling, release, registry), parallelism, TestContext.Current.CancellationToken);

        await spilling.Task.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Exception failure = await ReleaseAndCaptureAsync(runTask, release, registry);
        Assert.IsType<MalformedLineException>(failure);
    }

    [Theory]
    [MemberData(nameof(RunGenerationStrategyTests.Strategies), MemberType = typeof(RunGenerationStrategyTests))]
    [Trait("Case", "SL-05")]
    public async Task Cancellation_does_not_end_the_run_while_a_spill_is_still_running(
        RunGenerationStrategyTests.Strategy strategy)
    {
        // Only the first spill is gated: gating all would stall the reader so the token is never
        // observed, and a strategy that joins nothing would pass too.
        const int parallelism = 2;
        using ParkingStream input = new(BuildLines(count: 20));
        BufferPool pool = new(bufferSize: 128, descriptorCapacity: 2, capacity: parallelism + 2);
        await using ChunkReader reader = new(input, pool, maxLineLength: 32);
        RunRegistry registry = new();

        TaskCompletionSource spilling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int started = 0;
        ChunkSpill spill = async (chunk, ct) =>
        {
            try
            {
                if (Interlocked.Increment(ref started) == 1)
                {
                    spilling.SetResult();

                    // Not ct: a spill that abandons on cancellation would join trivially.
                    await release.Task;
                }

                return registry.CreateRunPath();
            }
            finally
            {
                chunk.Buffer.Dispose();
            }
        };

        RunGenerationStrategy run = Resolve(strategy);
        using CancellationTokenSource cts = new();
        Task<IReadOnlyList<string>> runTask = run(reader, spill, parallelism, cts.Token);

        await spilling.Task.WaitAsync(BoundedWait, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        Exception failure = await ReleaseAndCaptureAsync(runTask, release, registry);
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
    }

    private static async Task<Exception> ReleaseAndCaptureAsync(
        Task<IReadOnlyList<string>> runTask, TaskCompletionSource release, RunRegistry registry)
    {
        await Task.WhenAny(runTask, Task.Delay(UnjoinedReturnWindow, TestContext.Current.CancellationToken));
        Assert.False(
            runTask.IsCompleted,
            "the strategy finished while a spill it had started was still holding a chunk and still able to create a run");

        release.SetResult();

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(
            () => runTask.WaitAsync(BoundedWait, TestContext.Current.CancellationToken));

        registry.Close();
        Assert.Equal(0, registry.CreatedAfterClose);
        Assert.True(registry.Created > 0, "no spill ever reached the registry, so the assertion above proves nothing");
        return failure;
    }

    // Waits on the gate, not ct: a spill that abandons on cancellation would join trivially.
    private static ChunkSpill HeldSpill(TaskCompletionSource spilling, TaskCompletionSource release, RunRegistry registry) =>
        async (chunk, ct) =>
        {
            try
            {
                spilling.TrySetResult();
                await release.Task;
                return registry.CreateRunPath();
            }
            finally
            {
                chunk.Buffer.Dispose();
            }
        };

    private RunGenerationStrategy Resolve(RunGenerationStrategyTests.Strategy strategy) => strategy switch
    {
        RunGenerationStrategyTests.Strategy.Akka => AkkaRunGeneration.Strategy(_akka.Materializer),
        RunGenerationStrategyTests.Strategy.Channels => ChannelRunGeneration.RunAsync,
        _ => throw new ArgumentOutOfRangeException(nameof(strategy)),
    };

    private static byte[] BuildLines(int count) =>
        Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, count).Select(i => $"{i}. Line number {i}\n")));

    private static async Task<long> WaitForStableLinesReadAsync(ChunkReader reader, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        long last = reader.LinesRead;
        int stableStreak = 0;
        while (stableStreak < 20)
        {
            await Task.Delay(5);
            long current = reader.LinesRead;
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
                throw new TimeoutException($"LinesRead never stabilised within {timeout} (last observed {last}).");
            }
        }

        return last;
    }

    // SL-05 needs a read outstanding when the token fires; a MemoryStream reaches EOF too fast.
    private sealed class ParkingStream(byte[] prefix) : Stream
    {
        private readonly MemoryStream _prefix = new(prefix);

        public override bool CanRead  => true;
        public override bool CanSeek  => false;
        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await _prefix.ReadAsync(buffer, cancellationToken);
            if (read > 0)
            {
                return read;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _prefix.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class RunRegistry
    {
        private int _closed;
        private int _created;
        private int _createdAfterClose;

        public int Created => Volatile.Read(ref _created);

        public int CreatedAfterClose => Volatile.Read(ref _createdAfterClose);

        public void Close() => Volatile.Write(ref _closed, 1);

        public string CreateRunPath()
        {
            if (Volatile.Read(ref _closed) == 1)
            {
                Interlocked.Increment(ref _createdAfterClose);
            }

            return $"run-{Interlocked.Increment(ref _created):D8}.tmp";
        }
    }

    private static async Task WaitForLinesReadBeyondAsync(ChunkReader reader, long floor, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (reader.LinesRead <= floor)
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"LinesRead never advanced past {floor} within {timeout}.");
            }

            await Task.Delay(5);
        }
    }
}
