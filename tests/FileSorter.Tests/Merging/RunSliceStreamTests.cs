using System.Text;
using CsCheck;
using FileSorter.LineFormat;
using FileSorter.Merging;
using Xunit;

namespace FileSorter.Tests.Merging;

/// <summary>
/// The end bound a range-partitioned merge worker reads its runs through. The cases are
/// the same class the double-buffered read-ahead needs -- a window that ends on descriptor
/// capacity exactly at the slice boundary, three of those in a row, a slice whose last
/// line ends exactly at the bound, an empty slice, and a slice starting mid-file -- and
/// they are driven against the PAIR, because the bound lives in
/// <see cref="RunSliceStream"/> and everything that reacts to it lives in
/// <c>RunCursor</c>.
///
/// The line width and the window are chosen so a window holds a whole number of lines
/// with nothing carried over: that is what makes "the window ended exactly here" a
/// property of the fixture rather than a hope, and it is the alignment under which
/// end-of-stream handling is most likely to go wrong, since a fill that returns exactly
/// what was asked for is indistinguishable from one with more behind it until the next
/// fill returns nothing.
/// </summary>
public sealed class RunSliceStreamTests : IDisposable
{
    // "0. aaaa\n" -- seven content bytes and a terminator.
    private const int LineWidth = 8;
    private const int MaxLineLength = 8;

    // Exactly four lines, and above RunCursor's own floor (maxLineLength + 2 = 10).
    private const int WindowSize = 4 * LineWidth;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "FileSorterTests", Guid.NewGuid().ToString("N"));

    public RunSliceStreamTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Case", "RC-01")]
    public async Task A_window_ending_on_descriptor_capacity_exactly_at_the_slice_boundary_delivers_every_line_and_stops()
    {
        // Four lines of eight bytes fill the window edge to edge and fill the descriptor
        // array at the same instant, and the slice ends there too. All three ends
        // coincide, which is the case where "the descriptor array filled" and "the slice
        // is over" are easiest to confuse for each other.
        await AssertSliceAsync(lineCount: 20, firstLine: 4, lineCountInSlice: 4, descriptorCapacity: 4);
    }

    [Fact]
    [Trait("Case", "RC-02")]
    public async Task Three_capacity_ended_windows_in_a_row_land_on_the_slice_boundary()
    {
        // The same coincidence three times over, so the second and third windows are
        // reached through the prefetch path rather than the first window's awaited fill.
        await AssertSliceAsync(lineCount: 30, firstLine: 6, lineCountInSlice: 12, descriptorCapacity: 4);
    }

    [Fact]
    [Trait("Case", "RC-03")]
    public async Task A_slice_whose_last_line_ends_exactly_at_the_bound_without_filling_the_descriptor_array()
    {
        // The other half of the case above: the bytes end exactly at the bound but the
        // descriptor array had room to spare, so the window ends for the other of the
        // two reasons a window can end.
        await AssertSliceAsync(lineCount: 20, firstLine: 5, lineCountInSlice: 4, descriptorCapacity: 7);
    }

    [Fact]
    [Trait("Case", "RC-04")]
    public async Task An_empty_slice_delivers_no_lines()
    {
        // The partition routinely produces these -- a worker whose key range no run
        // holds a line in, and every worker of a run of byte-identical lines but one.
        await AssertSliceAsync(lineCount: 10, firstLine: 3, lineCountInSlice: 0, descriptorCapacity: 4);
    }

    [Fact]
    [Trait("Case", "RC-05")]
    public async Task A_slice_starting_mid_file_delivers_its_own_lines_and_no_neighbours()
    {
        // Not aligned to the window, so every window boundary inside the slice sits at a
        // different phase of the file than it would at offset zero -- which is what makes
        // this different from reading the whole run and stopping early.
        await AssertSliceAsync(lineCount: 25, firstLine: 7, lineCountInSlice: 11, descriptorCapacity: 3);
    }

    [Fact]
    [Trait("Case", "RC-06")]
    public async Task A_slice_reaching_the_end_of_the_file_delivers_the_final_line()
    {
        // The last worker's slice always ends at the run's own end, so the bound and the
        // genuine end of stream coincide -- the one case where RunCursor's own
        // exhausted-stream path and the bound are reached together.
        await AssertSliceAsync(lineCount: 13, firstLine: 9, lineCountInSlice: 4, descriptorCapacity: 4);
    }

    [Fact]
    [Trait("Case", "RC-07")]
    public async Task Every_slice_of_a_random_file_at_a_random_window_delivers_exactly_its_own_lines()
    {
        // The cases above pin the alignments someone thought of; this sweeps the ones
        // nobody did, over the two dimensions that decide where a window ends (window
        // size and descriptor capacity) and the two that decide where a slice does (its
        // first and last line).
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
        // The trap: awaiting the inner stream's DisposeAsync and then calling
        // base.DisposeAsync() disposes it twice, because Stream's default
        // DisposeAsync calls Dispose(true) synchronously and this class's Dispose
        // override disposes the inner stream. A plain MemoryStream's Dispose is
        // idempotent and would hide that, so the counting stream is what makes
        // "disposed exactly once" an assertion rather than a hope.
        DisposeCountingStream inner = new(new MemoryStream(new byte[8]));
        RunSliceStream slice = new(inner, 2, 6);

        await slice.DisposeAsync();

        Assert.Equal(1, inner.DisposeCount + inner.DisposeAsyncCount);
    }

    /// Writes a file of `lineCount` fixed-width lines, reads the slice holding lines
    /// `[firstLine, firstLine + lineCountInSlice)` through a <see cref="RunSliceStream"/>
    /// and a `RunCursor`, and requires exactly those lines back.
    private async Task AssertSliceAsync(
        int lineCount, int firstLine, int lineCountInSlice, int descriptorCapacity, int windowSize = WindowSize)
    {
        string path = Path.Combine(_directory, $"run-{Guid.NewGuid():N}.tmp");
        string[] lines = [.. Enumerable.Range(0, lineCount).Select(i => $"{i:D2}. {(char)('a' + (i % 26))}{i % 10:D2}")];
        StringBuilder content = new();
        foreach (string line in lines)
        {
            content.Append(line).Append('\n');
        }

        byte[] bytes = Encoding.ASCII.GetBytes(content.ToString());
        Assert.All(lines, line => Assert.Equal(LineWidth - 1, line.Length)); // fixed width, as the fixture claims
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);

        long start = (long)firstLine * LineWidth;
        long end = start + ((long)lineCountInSlice * LineWidth);

        // The cursor owns the slice stream and the slice stream owns the file, exactly as
        // in MergeExecutor.MergeSliceAsync, so disposing the cursor is what closes the
        // handle -- there is no second owner here to hide a leak behind.
        List<string> delivered = [];
        FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.Asynchronous);
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
