using FileSorter.Merging;
using Xunit;

namespace FileSorter.Tests.Merging;

/// <summary>
/// The end-offset guard every range-partitioned merge worker writes through.
///
/// The guard is tested here rather than through <c>MergeExecutor</c> deliberately.
/// Reaching it through the executor would need a partition that mispredicts a slice
/// length upward, which is exactly the defect the partition's own arithmetic makes
/// impossible -- so an executor-level test could only reach it by constructing a
/// partition the production code cannot produce. What the executor DOES check, and what
/// <c>MergeExecutorTests</c> covers, is the other half of the pair: the per-worker length
/// check that fires when a worker writes too FEW bytes, which no guard on a write can see.
/// </summary>
public sealed class OutputSliceStreamTests
{
    [Fact]
    [Trait("Case", "OS-01")]
    public async Task Writes_land_at_the_slice_start_rather_than_at_the_start_of_the_file()
    {
        MemoryStream backing = new(new byte[16], writable: true);
        OutputSliceStream slice = new(backing, 4, 8);

        await slice.WriteAsync(new byte[] { 1, 2, 3, 4 }, TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 0, 0, 0, 0, 1, 2, 3, 4, 0, 0, 0, 0, 0, 0, 0, 0 }, backing.ToArray());
        Assert.Equal(4, slice.BytesWritten);
    }

    [Fact]
    [Trait("Case", "OS-02")]
    public async Task A_write_that_would_cross_the_end_of_the_slice_throws_and_writes_nothing()
    {
        // The failure this exists to catch: a worker that emitted one line too many would
        // otherwise overwrite the first line of its neighbour's range, leaving a file of
        // exactly the right total length that is wrong in two places at once -- which the
        // sequential merge's single Position == totalBytes check cannot see.
        MemoryStream backing = new(new byte[16], writable: true);
        OutputSliceStream slice = new(backing, 4, 8);

        await slice.WriteAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await slice.WriteAsync(new byte[] { 4, 5 }, TestContext.Current.CancellationToken));

        Assert.Contains("past the end of its own output slice", ex.Message, StringComparison.Ordinal);
        Assert.Equal(3, slice.BytesWritten);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 1, 2, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, backing.ToArray());
    }

    [Fact]
    [Trait("Case", "OS-03")]
    public async Task A_write_that_fills_the_slice_exactly_is_allowed()
    {
        // The boundary the guard has to get right in the other direction: every worker's
        // last write ends exactly at its own end, so an off-by-one here would fail every
        // partitioned merge rather than none.
        MemoryStream backing = new(new byte[8], writable: true);
        OutputSliceStream slice = new(backing, 2, 6);

        await slice.WriteAsync(new byte[] { 1, 2 }, TestContext.Current.CancellationToken);
        await slice.WriteAsync(new byte[] { 3, 4 }, TestContext.Current.CancellationToken);

        Assert.Equal(4, slice.BytesWritten);
        Assert.Equal(new byte[] { 0, 0, 1, 2, 3, 4, 0, 0 }, backing.ToArray());
    }

    [Fact]
    [Trait("Case", "OS-04")]
    public async Task An_empty_slice_accepts_nothing_at_all()
    {
        // Empty slices are routine (a worker whose key range no run holds a line in), and
        // a worker handed one must write nothing rather than one stray terminator.
        MemoryStream backing = new(new byte[4], writable: true);
        OutputSliceStream slice = new(backing, 2, 2);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await slice.WriteAsync(new byte[] { 1 }, TestContext.Current.CancellationToken));

        Assert.Equal(0, slice.BytesWritten);
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutputSliceStream(backing, 3, 2));
    }

    [Fact]
    [Trait("Case", "OS-05")]
    public async Task Disposing_the_slice_disposes_the_inner_stream_exactly_once()
    {
        // The trap: awaiting the inner stream's DisposeAsync and then calling
        // base.DisposeAsync() disposes it twice, because Stream's default
        // DisposeAsync calls Dispose(true) synchronously and this class's Dispose
        // override disposes the inner stream. A plain MemoryStream's Dispose is
        // idempotent and would hide that, so the counting stream is what makes
        // "disposed exactly once" an assertion rather than a hope.
        DisposeCountingStream inner = new(new MemoryStream(new byte[8]));
        OutputSliceStream slice = new(inner, 2, 6);

        await slice.DisposeAsync();

        Assert.Equal(1, inner.DisposeCount + inner.DisposeAsyncCount);
    }
}
