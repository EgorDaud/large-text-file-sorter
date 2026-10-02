using FileSorter.Merging;
using Xunit;

namespace FileSorter.Tests.Merging;

// Tested directly: a valid partition never overflows a slice, so MergeExecutor cannot reach this guard.
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
        DisposeCountingStream inner = new(new MemoryStream(new byte[8]));
        OutputSliceStream slice = new(inner, 2, 6);

        await slice.DisposeAsync();

        Assert.Equal(1, inner.DisposeCount + inner.DisposeAsyncCount);
    }
}
