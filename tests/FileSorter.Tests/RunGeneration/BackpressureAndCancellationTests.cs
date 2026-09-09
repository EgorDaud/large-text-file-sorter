using System.Diagnostics;
using System.Text;
using FileSorter.LineFormat;
using FileSorter.RunGeneration;
using Xunit;

namespace FileSorter.Tests.RunGeneration;

/// <summary>
/// The three claims <see cref="RunGenerationStrategyTests"/> cannot make: that the pool is
/// load-bearing -- a spiller held open genuinely stalls the reader, rather than the
/// ceiling merely never being observed to be exceeded -- that cancellation is prompt
/// rather than eventual, and that a run is over when the strategy says it is, with no
/// spill of its own still running against resources its caller is about to dispose. Both
/// strategies run the same cases, because a behaviour holding for one scheduler and not
/// the other is a defect, not a library quirk.
/// </summary>
public sealed class BackpressureAndCancellationTests : IClassFixture<AkkaFixture>
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(15);

    // An upper bound on how long a prompt cancellation may take on a saturated thread
    // pool, not an expectation of how long one normally takes. It stays under
    // BoundedWait so a cancellation that never unwinds fails this assertion rather than
    // being hidden behind the outer timeout.
    private static readonly TimeSpan CancellationResponsivenessBound = TimeSpan.FromSeconds(10);

    // How long a strategy that walks away from a spill it started is given to do so
    // before the join cases below check that it has not. This is not a timing assumption
    // on the passing side: a strategy that joins its spills cannot complete while the
    // gate is held, however long or short this wait is. It only decides how reliably a
    // strategy that does not join is caught here rather than by the run-registry
    // assertion that follows it.
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

        // The one spiller this case holds open. Every chunk after the first has
        // nowhere to go once the pool fills, since parallelism 1 means nothing else
        // is draining it.
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

        // How far a strategy lets the reader run ahead of the one chunk inside spill is
        // its own internal buffering: Channels fills the pool to its ceiling, while
        // Akka's SelectAsyncUnordered at parallelism 1 requests no further input until
        // its one in-flight operation completes and so stalls holding only the blocked
        // chunk's buffer. Either way at least one buffer is held, and the stall itself
        // is what stalledAt < totalLines above proves.
        Assert.InRange(pool.Outstanding, 1, poolCapacity);

        heldOpen.SetResult();

        // Progress resumes: LinesRead moves past where it stalled, and the run goes on
        // to complete normally rather than the held-open spill having wedged it for good.
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

        // At two lines per chunk (descriptorCapacity below) and a 50 ms spill delay
        // split across two workers, draining every chunk this input produces would take
        // roughly (1000 / 2) * 50 / 2 ms ~ 12.5 seconds if cancellation did nothing.
        // Cancelling shortly after the start and requiring the unwind to finish inside
        // CancellationResponsivenessBound is what makes "responsive" falsifiable.
        using MemoryStream input = new(BuildLines(totalLines));
        BufferPool pool = new(bufferSize: 128, descriptorCapacity: 2, capacity: parallelism + 2);
        await using ChunkReader reader = new(input, pool, maxLineLength: 32);

        ChunkSpill spill = async (chunk, ct) =>
        {
            try
            {
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

        // Long enough for the run to be underway, past its first few chunks, and short
        // enough to stay tiny next to the full drain above.
        await Task.Delay(30, TestContext.Current.CancellationToken);

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

                    // Waits on the gate rather than on ct: this stands in for a spill
                    // already inside a write it cannot abandon, which is the sibling a
                    // strategy has to join rather than walk away from.
                    await release.Task;
                    return registry.CreateRunPath();
                }

                // Held until the sibling is genuinely in flight, so the case cannot pass
                // by there having been no sibling to join in the first place.
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
        // The failure comes from the reader rather than from a spill, which is the other
        // half of the same claim: the chunk before the malformed one is already out with
        // a spiller, and that spiller has to be joined too.
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
        // Cancellation is the path where walking away is most tempting and least
        // acceptable: Program's own cancellation handler disposes the temporary-run set
        // immediately afterwards, so a spill still running then is a run file created
        // after the cleanup that was supposed to remove it.
        //
        // Only the first spill is gated, as in SL-03, and every later one finishes at
        // once. Gating all of them instead saturates the pipeline at parallelism 2:
        // nothing asks the reader for another chunk, so nothing observes the token, and
        // the strategy stays unfinished for want of a failure rather than because it
        // joined anything -- which would make the case pass against a strategy that
        // joins nothing at all. With one slot always free the reader keeps being pulled,
        // and the parking stream keeps a read outstanding for the token to fault.
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

                    // Waits on the gate rather than on ct, for SL-03's reason: a spill
                    // that abandons its work the instant cancellation is requested joins
                    // trivially, and would prove nothing about a strategy that does not
                    // wait.
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

    // The half the three cases above share: the strategy must still be running while the
    // gate is held, and once it has returned, nothing it started may touch the caller's
    // resources -- modelled by closing the run registry the instant the strategy's task
    // completes, exactly as Program disposes TemporaryRunSet on the way out.
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

    // A spill that finishes only when the gate is released, and reaches the registry only
    // once it does. It waits on the gate rather than on its own token deliberately: a
    // spill that abandons its work the moment cancellation is requested would join
    // trivially, and would prove nothing about a strategy that does not wait.
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
        RunGenerationStrategyTests.Strategy.Akka => (reader, spill, parallelism, ct) =>
            AkkaRunGeneration.RunAsync(reader, spill, parallelism, _akka.Materializer, ct),
        RunGenerationStrategyTests.Strategy.Channels => ChannelRunGeneration.RunAsync,
        _ => throw new ArgumentOutOfRangeException(nameof(strategy)),
    };

    private static byte[] BuildLines(int count) =>
        Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, count).Select(i => $"{i}. Line number {i}\n")));

    // Polls LinesRead until it has not moved for twenty consecutive samples, then
    // returns that value: how long a strategy takes to stall is strategy-dependent, so
    // a fixed delay would be a guess.
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

    // Hands over a fixed prefix and then parks: once the prefix is drained, every further
    // read completes only by being cancelled. SL-05 needs a read to be outstanding when
    // the token is cancelled, and a MemoryStream cannot supply one -- it reaches its end
    // in microseconds, after which nothing is left for the token to interrupt and whether
    // the case tests anything would turn on that race.
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

    // Stands in for TemporaryRunSet, which Program disposes the moment a strategy
    // returns: a run created after Close is a file created after the cleanup that would
    // have removed it, which is precisely what a spill outliving its strategy does.
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
