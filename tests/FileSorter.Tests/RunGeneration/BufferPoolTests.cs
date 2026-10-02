using FileSorter.RunGeneration;
using Xunit;
using static FileSorter.Tests.Support.TestTimeouts;

namespace FileSorter.Tests.RunGeneration;

public sealed class BufferPoolTests
{
    [Fact]
    [Trait("Case", "BP-01")]
    public async Task Acquiring_and_releasing_round_trips_a_usable_buffer()
    {
        BufferPool pool = new(bufferSize: 16, descriptorCapacity: 4, capacity: 1);

        PooledBuffer first = await pool.AcquireAsync(TestContext.Current.CancellationToken);
        Assert.Equal(16, first.Bytes.Length);
        first.Dispose();

        PooledBuffer second = await pool.AcquireAsync(TestContext.Current.CancellationToken);
        Assert.Equal(16, second.Bytes.Length);
        second.Dispose();
    }

    [Fact]
    [Trait("Case", "BP-02")]
    public async Task Acquiring_from_an_exhausted_pool_waits_rather_than_allocating()
    {
        BufferPool pool = new(bufferSize: 16, descriptorCapacity: 4, capacity: 1);
        PooledBuffer held = await pool.AcquireAsync(TestContext.Current.CancellationToken);

        Task<PooledBuffer> pending = pool.AcquireAsync(TestContext.Current.CancellationToken).AsTask();

        Assert.False(pending.IsCompleted);
        Assert.Equal(1, pool.Outstanding);

        held.Dispose();
        (await pending.WaitAsync(BoundedWait, TestContext.Current.CancellationToken)).Dispose();
    }

    [Fact]
    [Trait("Case", "BP-03")]
    public async Task Releasing_a_buffer_completes_a_deferred_acquisition()
    {
        BufferPool pool = new(bufferSize: 16, descriptorCapacity: 4, capacity: 1);
        PooledBuffer held = await pool.AcquireAsync(TestContext.Current.CancellationToken);
        Task<PooledBuffer> pending = pool.AcquireAsync(TestContext.Current.CancellationToken).AsTask();

        held.Dispose();
        Task completedFirst = await Task.WhenAny(pending, Task.Delay(BoundedWait, TestContext.Current.CancellationToken));

        Assert.Same(pending, completedFirst);
        (await pending).Dispose();
    }

    [Fact]
    [Trait("Case", "BP-04")]
    public async Task A_buffer_is_returned_when_the_consumer_fails()
    {
        BufferPool pool = new(bufferSize: 16, descriptorCapacity: 4, capacity: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            PooledBuffer buffer = await pool.AcquireAsync(TestContext.Current.CancellationToken);
            try
            {
                throw new InvalidOperationException("simulated consumer failure");
            }
            finally
            {
                buffer.Dispose();
            }
        });

        Task<PooledBuffer> next = pool.AcquireAsync(TestContext.Current.CancellationToken).AsTask();
        Task completedFirst = await Task.WhenAny(next, Task.Delay(BoundedWait, TestContext.Current.CancellationToken));
        Assert.Same(next, completedFirst);
        (await next).Dispose();
    }

    [Fact]
    [Trait("Case", "BP-05")]
    public async Task The_same_buffer_is_never_outstanding_to_two_consumers_at_once()
    {
        BufferPool pool = new(bufferSize: 16, descriptorCapacity: 4, capacity: 3);
        HashSet<byte[]> outstanding = [];

        for (int cycle = 0; cycle < 20; cycle++)
        {
            PooledBuffer a = await pool.AcquireAsync(TestContext.Current.CancellationToken);
            PooledBuffer b = await pool.AcquireAsync(TestContext.Current.CancellationToken);
            Assert.True(outstanding.Add(a.Bytes));
            Assert.True(outstanding.Add(b.Bytes));

            a.Dispose();
            b.Dispose();
            outstanding.Remove(a.Bytes);
            outstanding.Remove(b.Bytes);
        }
    }

    [Fact]
    [Trait("Case", "BP-06")]
    public async Task The_ceiling_is_respected_under_concurrent_pressure()
    {
        const int ceiling = 4;
        BufferPool pool = new(bufferSize: 16, descriptorCapacity: 4, capacity: ceiling);
        int observedMax = 0;

        IEnumerable<Task> consumers = Enumerable.Range(0, ceiling * 5).Select(async _ =>
        {
            PooledBuffer buffer = await pool.AcquireAsync(TestContext.Current.CancellationToken);
            try
            {
                int current = pool.Outstanding;
                int prior;
                do
                {
                    prior = Volatile.Read(ref observedMax);
                    if (current <= prior)
                    {
                        break;
                    }
                }
                while (Interlocked.CompareExchange(ref observedMax, current, prior) != prior);

                await Task.Delay(5);
            }
            finally
            {
                buffer.Dispose();
            }
        });

        await Task.WhenAll(consumers).WaitAsync(BoundedWait, TestContext.Current.CancellationToken);

        Assert.True(observedMax <= ceiling);
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    [Trait("Case", "BP-07")]
    public async Task Releasing_a_foreign_or_already_released_buffer_fails_loudly()
    {
        BufferPool pool = new(bufferSize: 16, descriptorCapacity: 4, capacity: 1);

        PooledBuffer buffer = await pool.AcquireAsync(TestContext.Current.CancellationToken);
        buffer.Dispose();
        Assert.Throws<InvalidOperationException>(buffer.Dispose);

        PooledBuffer neverIssued = new(pool, slot: 0, bytes: new byte[1], lines: []);
        Assert.Throws<InvalidOperationException>(neverIssued.Dispose);
    }

    [Fact]
    [Trait("Case", "BP-08")]
    public async Task Every_issued_buffer_is_at_least_the_configured_size()
    {
        BufferPool pool = new(bufferSize: 128, descriptorCapacity: 4, capacity: 2);

        PooledBuffer a = await pool.AcquireAsync(TestContext.Current.CancellationToken);
        PooledBuffer b = await pool.AcquireAsync(TestContext.Current.CancellationToken);

        Assert.Equal(128, a.Bytes.Length);
        Assert.Equal(128, b.Bytes.Length);
        Assert.Equal(128, pool.BufferSize);

        a.Dispose();
        b.Dispose();
    }

    [Fact]
    [Trait("Case", "BP-09")]
    public async Task A_ceiling_of_one_serialises_two_sequential_consumers()
    {
        BufferPool pool = new(bufferSize: 16, descriptorCapacity: 4, capacity: 1);

        PooledBuffer first = await pool.AcquireAsync(TestContext.Current.CancellationToken);
        first.Dispose();
        PooledBuffer second = await pool.AcquireAsync(TestContext.Current.CancellationToken);
        second.Dispose();

        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    [Trait("Case", "BP-10")]
    public async Task A_reissued_buffer_may_still_hold_a_previous_consumers_bytes_beyond_its_recorded_length()
    {
        BufferPool pool = new(bufferSize: 16, descriptorCapacity: 4, capacity: 1);

        PooledBuffer first = await pool.AcquireAsync(TestContext.Current.CancellationToken);
        Array.Fill(first.Bytes, (byte)'X');
        first.Dispose();

        PooledBuffer second = await pool.AcquireAsync(TestContext.Current.CancellationToken);
        second.Bytes[0] = (byte)'A';
        int recordedLength = 1;

        Assert.Equal((byte)'A', second.Bytes[0]);
        Assert.Equal((byte)'X', second.Bytes[recordedLength]);
        second.Dispose();
    }
}
