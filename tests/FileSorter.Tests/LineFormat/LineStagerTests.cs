using FileSorter.LineFormat;
using Xunit;

namespace FileSorter.Tests.LineFormat;

public sealed class LineStagerTests
{
    // The stager reuses its buffer, so each write must be copied when it happens.
    private static LineStager CreateStager(int bufferSize, List<string> writes) =>
        new(new byte[bufferSize], data =>
        {
            writes.Add(System.Text.Encoding.ASCII.GetString(data.Span));
            return ValueTask.CompletedTask;
        });

    [Fact]
    [Trait("Case", "CM-11")]
    public async Task A_fitting_line_is_staged_with_a_line_feed_and_written_once_on_flush()
    {
        List<string> writes = [];
        LineStager stager = CreateStager(16, writes);

        Assert.True(stager.TryAdd("1. Apple"u8));
        Assert.Empty(writes);

        await stager.FlushAsync();
        Assert.Equal(["1. Apple\n"], writes);
    }

    [Fact]
    [Trait("Case", "CM-11")]
    public async Task Lines_that_fill_the_buffer_exactly_are_staged_without_a_write()
    {
        List<string> writes = [];
        LineStager stager = CreateStager(12, writes);

        Assert.True(stager.TryAdd("1. Abc"u8));
        Assert.True(stager.TryAdd("2. B"u8));
        Assert.Empty(writes);

        await stager.FlushAsync();
        Assert.Equal(["1. Abc\n2. B\n"], writes);
    }

    [Fact]
    [Trait("Case", "CM-11")]
    public async Task A_line_that_overflows_flushes_the_staged_lines_once_and_is_staged_afterwards()
    {
        List<string> writes = [];
        LineStager stager = CreateStager(12, writes);
        Assert.True(stager.TryAdd("1. Abc"u8));
        Assert.True(stager.TryAdd("2. B"u8));

        byte[] source = "3. Bcd"u8.ToArray();
        Assert.False(stager.TryAdd(source));
        Assert.Empty(writes);

        await stager.AddAfterFlushAsync(source, 0, source.Length);
        Assert.Equal(["1. Abc\n2. B\n"], writes);

        await stager.FlushAsync();
        Assert.Equal(["1. Abc\n2. B\n", "3. Bcd\n"], writes);
    }

    [Fact]
    [Trait("Case", "CM-11")]
    public async Task A_line_larger_than_the_buffer_is_written_directly_after_the_staged_lines()
    {
        List<string> writes = [];
        LineStager stager = CreateStager(8, writes);
        Assert.True(stager.TryAdd("1. A"u8));

        byte[] source = "1. Apple"u8.ToArray();
        Assert.False(stager.TryAdd(source));
        await stager.AddAfterFlushAsync(source, 0, source.Length);
        Assert.Equal(["1. A\n", "1. Apple", "\n"], writes);

        await stager.FlushAsync();
        Assert.Equal(3, writes.Count);
        Assert.True(stager.TryAdd("2. B"u8));
    }

    [Fact]
    [Trait("Case", "CM-11")]
    public async Task A_line_larger_than_an_empty_buffer_is_written_directly_without_flushing_anything()
    {
        List<string> writes = [];
        LineStager stager = CreateStager(8, writes);

        byte[] source = "xx1. Apple"u8.ToArray();
        Assert.False(stager.TryAdd(source.AsSpan(2)));
        await stager.AddAfterFlushAsync(source, 2, 8);

        Assert.Equal(["1. Apple", "\n"], writes);
    }

    [Fact]
    [Trait("Case", "CM-11")]
    public async Task Flushing_an_empty_stager_does_not_write()
    {
        List<string> writes = [];
        LineStager stager = CreateStager(8, writes);

        await stager.FlushAsync();

        Assert.Empty(writes);
    }
}
