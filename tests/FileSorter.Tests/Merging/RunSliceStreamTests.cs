using System.Text;
using CsCheck;
using FileSorter.Infrastructure;
using FileSorter.LineFormat;
using FileSorter.Merging;
using FileSorter.Tests.Support;
using Xunit;

namespace FileSorter.Tests.Merging;

public sealed class RunSliceStreamTests : IDisposable
{
    // "00. a00\n"
    private const int LineWidth = 8;
    private const int MaxLineLength = 8;

    // Exactly four whole lines, and above RunCursor's floor of maxLineLength + 2.
    private const int WindowSize = 4 * LineWidth;

    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    [Trait("Case", "RC-01")]
    public async Task A_window_ending_on_descriptor_capacity_exactly_at_the_slice_boundary_delivers_every_line_and_stops()
    {
        await AssertSliceAsync(lineCount: 20, firstLine: 4, lineCountInSlice: 4, descriptorCapacity: 4);
    }

    [Fact]
    [Trait("Case", "RC-02")]
    public async Task Three_capacity_ended_windows_in_a_row_land_on_the_slice_boundary()
    {
        await AssertSliceAsync(lineCount: 30, firstLine: 6, lineCountInSlice: 12, descriptorCapacity: 4);
    }

    [Fact]
    [Trait("Case", "RC-03")]
    public async Task A_slice_whose_last_line_ends_exactly_at_the_bound_without_filling_the_descriptor_array()
    {
        await AssertSliceAsync(lineCount: 20, firstLine: 5, lineCountInSlice: 4, descriptorCapacity: 7);
    }

    [Fact]
    [Trait("Case", "RC-04")]
    public async Task An_empty_slice_delivers_no_lines()
    {
        await AssertSliceAsync(lineCount: 10, firstLine: 3, lineCountInSlice: 0, descriptorCapacity: 4);
    }

    [Fact]
    [Trait("Case", "RC-05")]
    public async Task A_slice_starting_mid_file_delivers_its_own_lines_and_no_neighbours()
    {
        await AssertSliceAsync(lineCount: 25, firstLine: 7, lineCountInSlice: 11, descriptorCapacity: 3);
    }

    [Fact]
    [Trait("Case", "RC-06")]
    public async Task A_slice_reaching_the_end_of_the_file_delivers_the_final_line()
    {
        await AssertSliceAsync(lineCount: 13, firstLine: 9, lineCountInSlice: 4, descriptorCapacity: 4);
    }

    [Fact]
    [Trait("Case", "RC-07")]
    public async Task Every_slice_of_a_random_file_at_a_random_window_delivers_exactly_its_own_lines()
    {
        Gen<(int LineCount, int First, int Length, int Window, int Descriptors)> scenario =
            Gen.Select(Gen.Int[0, 60], Gen.Int[0, 60], Gen.Int[0, 60], Gen.Int[MaxLineLength + 2, 64], Gen.Int[1, 9]);

        await scenario.SampleAsync(async draw =>
        {
            int first = draw.LineCount == 0 ? 0 : draw.First % (draw.LineCount + 1);
            int length = Math.Min(draw.Length, draw.LineCount - first);
            await AssertSliceAsync(draw.LineCount, first, length, draw.Descriptors, draw.Window);
        }, iter: 400);
    }

    [Fact]
    [Trait("Case", "RC-08")]
    public async Task Disposing_the_slice_disposes_the_inner_stream_exactly_once()
    {
        DisposeCountingStream inner = new(new MemoryStream(new byte[8]));
        RunSliceStream slice = new(inner, 2, 6);

        await slice.DisposeAsync();

        Assert.Equal(1, inner.DisposeCount + inner.DisposeAsyncCount);
    }

    private async Task AssertSliceAsync(
        int lineCount, int firstLine, int lineCountInSlice, int descriptorCapacity, int windowSize = WindowSize)
    {
        string path = Path.Combine(_directory.Path, $"run-{Guid.NewGuid():N}.tmp");
        string[] lines = [.. Enumerable.Range(0, lineCount).Select(i => $"{i:D2}. {(char)('a' + (i % 26))}{i % 10:D2}")];
        StringBuilder content = new();
        foreach (string line in lines)
        {
            content.Append(line).Append('\n');
        }

        byte[] bytes = Encoding.ASCII.GetBytes(content.ToString());
        Assert.All(lines, line => Assert.Equal(LineWidth - 1, line.Length));
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);

        long start = (long)firstLine * LineWidth;
        long end = start + ((long)lineCountInSlice * LineWidth);

        List<string> delivered = [];
        FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: FileStreams.Unbuffered, FileOptions.Asynchronous);
        RunCursorBuffers buffers = new(
            new byte[windowSize], new byte[windowSize], new LineDescriptor[descriptorCapacity]);
        RunCursor cursor = new(new RunSliceStream(file, start, end), buffers, MaxLineLength);
        try
        {
            while (await cursor.MoveNextAsync(TestContext.Current.CancellationToken))
            {
                LineDescriptor descriptor = cursor.Current;
                delivered.Add(Encoding.ASCII.GetString(cursor.Buffer, descriptor.Offset, descriptor.Length));
            }
        }
        finally
        {
            await cursor.DisposeAsync();
        }

        Assert.Equal(lines.Skip(firstLine).Take(lineCountInSlice), delivered);
    }
}
