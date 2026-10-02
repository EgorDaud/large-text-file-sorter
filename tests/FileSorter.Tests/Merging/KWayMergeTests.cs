using System.Text;
using FileSorter.LineFormat;
using FileSorter.Merging;
using Xunit;

namespace FileSorter.Tests.Merging;

public sealed class KWayMergeTests
{
    [Fact]
    [Trait("Case", "KM-01")]
    public async Task Merges_a_single_run_unchanged()
    {
        List<string> result = await MergeAsync([RunOf("1. Apple", "2. Banana", "3. Cherry")]);

        Assert.Equal(["1. Apple", "2. Banana", "3. Cherry"], result);
    }

    [Fact]
    [Trait("Case", "KM-02")]
    public async Task Merges_two_interleaving_runs_into_one_ordered_sequence()
    {
        List<string> result = await MergeAsync(
        [
            RunOf("1. Apple", "3. Cherry", "5. Elderberry"),
            RunOf("2. Banana", "4. Date", "6. Fig"),
        ]);

        Assert.Equal(["1. Apple", "2. Banana", "3. Cherry", "4. Date", "5. Elderberry", "6. Fig"], result);
    }

    [Fact]
    [Trait("Case", "KM-03")]
    public async Task Merges_many_runs_correctly()
    {
        List<Stream> runs = [.. Enumerable.Range(0, 10).Select(r =>
            RunOf([.. Enumerable.Range(0, 10).Select(i => $"{r + i * 10}. L{r + i * 10:D3}")]))];

        List<string> result = await MergeAsync(runs);

        List<string> expected = [.. Enumerable.Range(0, 100).Select(v => $"{v}. L{v:D3}")];
        Assert.Equal(expected, result);
    }

    [Fact]
    [Trait("Case", "KM-04")]
    public async Task Merges_runs_of_wildly_unequal_length_without_stalling_the_long_one()
    {
        static string Format(int value) => $"{value}. L{value:D4}";

        int[] longValues = [.. Enumerable.Range(0, 500).Select(i => i * 3)];
        List<string> result = await MergeAsync(
        [
            RunOf([.. longValues.Select(Format)]),
            RunOf(Format(1)),
            RunOf(Format(2)),
        ]);

        string[] expected = [.. longValues.Append(1).Append(2).OrderBy(v => v).Select(Format)];
        Assert.Equal(expected, result);
        Assert.Equal(502, result.Count);
    }

    [Fact]
    [Trait("Case", "KM-05")]
    public async Task An_empty_run_at_the_first_position_contributes_nothing()
    {
        List<string> result = await MergeAsync(
        [
            RunOf(),
            RunOf("1. Apple", "2. Banana"),
        ]);

        Assert.Equal(["1. Apple", "2. Banana"], result);
    }

    [Fact]
    [Trait("Case", "KM-05")]
    public async Task An_empty_run_at_the_last_position_contributes_nothing()
    {
        List<string> result = await MergeAsync(
        [
            RunOf("1. Apple", "2. Banana"),
            RunOf(),
        ]);

        Assert.Equal(["1. Apple", "2. Banana"], result);
    }

    [Fact]
    [Trait("Case", "KM-06")]
    public async Task All_runs_empty_produces_empty_output()
    {
        List<string> result = await MergeAsync([RunOf(), RunOf(), RunOf()]);

        Assert.Empty(result);
    }

    [Fact]
    [Trait("Case", "KM-07")]
    public async Task Zero_runs_produces_empty_output()
    {
        List<string> result = await MergeAsync([]);

        Assert.Empty(result);
    }

    [Fact]
    [Trait("Case", "KM-08")]
    public async Task Duplicate_keys_spanning_three_runs_all_survive_adjacently()
    {
        List<string> result = await MergeAsync(
        [
            RunOf("5. Apple", "9. Zebra"),
            RunOf("5. Apple", "6. Mango"),
            RunOf("5. Apple", "7. Peach"),
        ]);

        Assert.Equal(6, result.Count);
        Assert.Equal(["5. Apple", "5. Apple", "5. Apple"], result[..3]);
        Assert.Equal(["6. Mango", "7. Peach", "9. Zebra"], result[3..]);
    }

    [Fact]
    [Trait("Case", "KM-09")]
    public async Task A_leading_zero_tie_across_runs_is_broken_by_raw_bytes_deterministically()
    {
        List<string> result = await MergeAsync(
        [
            RunOf("007. Apple", "9. Zebra"),
            RunOf("7. Apple", "8. Banana"),
        ]);

        Assert.Equal(["007. Apple", "7. Apple", "8. Banana", "9. Zebra"], result);
    }

    [Fact]
    [Trait("Case", "KM-09")]
    public async Task Fully_identical_heads_across_runs_both_survive_and_the_rest_still_orders_correctly()
    {
        List<string> result = await MergeAsync(
        [
            RunOf("5. Apple", "9. Zebra"),
            RunOf("5. Apple", "8. Banana"),
        ]);

        Assert.Equal(4, result.Count);
        Assert.Equal(["5. Apple", "5. Apple"], result[..2]);
        Assert.Equal(["8. Banana", "9. Zebra"], result[2..]);
    }

    [Fact]
    [Trait("Case", "KM-09")]
    public async Task Two_of_three_runs_share_a_byte_identical_line_and_both_copies_survive_adjacently()
    {
        List<string> result = await MergeAsync(
        [
            RunOf("5. Apple", "9. Zebra"),
            RunOf("5. Apple", "6. Mango"),
            RunOf("1. Ant", "8. Walnut"),
        ]);

        string[] expected = ["1. Ant", "5. Apple", "5. Apple", "6. Mango", "8. Walnut", "9. Zebra"];
        Assert.Equal(expected, result);
        Assert.Equal(["5. Apple", "5. Apple"], result[1..3]);
    }

    [Fact]
    [Trait("Case", "KM-10")]
    public async Task Every_input_line_is_advanced_exactly_once()
    {
        List<string> result = await MergeAsync(
        [
            RunOf("1. A", "4. D", "7. G"),
            RunOf("2. B", "5. E", "8. H"),
            RunOf("3. C", "6. F", "9. I"),
        ]);

        Assert.Equal(9, result.Count);
        Assert.Equal(["1. A", "2. B", "3. C", "4. D", "5. E", "6. F", "7. G", "8. H", "9. I"], result);
    }

    [Fact]
    [Trait("Case", "KM-11")]
    public async Task Merging_a_large_run_set_never_requests_a_read_larger_than_the_read_ahead_buffer()
    {
        const int bufferSize = 64;
        const int runCount = 4;
        const int linesPerRun = 2000;

        List<TrackingStream> runs = [.. Enumerable.Range(0, runCount).Select(r =>
            TrackingRunOf([.. Enumerable.Range(0, linesPerRun)
                .Select(i => r + i * runCount)
                .Select(v => $"{v}. L{v:D7}")]))];

        RunCursorBuffers[] cursorBuffers = [.. Enumerable.Range(0, runCount)
            .Select(_ => new RunCursorBuffers(new byte[bufferSize], new byte[bufferSize], new LineDescriptor[8]))];
        using MemoryStream output = new();
        byte[] outputStagingBuffer = new byte[64];
        await KWayMerge.MergeAsync(runs.Cast<Stream>().ToArray(), cursorBuffers, output, outputStagingBuffer,
            bufferSize - 2, ct: TestContext.Current.CancellationToken);

        List<string> result = ReadLines(output);

        string[] expected = [.. Enumerable.Range(0, runCount * linesPerRun).Select(v => $"{v}. L{v:D7}")];
        Assert.Equal(expected, result);

        Assert.All(runs, run => Assert.True(run.MaxRequestedRead <= bufferSize));
    }

    [Fact]
    [Trait("Case", "KM-15")]
    public async Task Lines_longer_than_the_staging_buffer_are_written_directly_mid_run_and_at_the_end()
    {
        string longMiddle = "2. " + new string('B', 80);
        string longLast = "4. " + new string('D', 90);
        string[] lines = ["1. Short", longMiddle, "3. Short2", longLast];

        const int readAheadBufferSize = 129;
        const int maxLineLength = 127;
        RunCursorBuffers[] cursorBuffers = [new RunCursorBuffers(
            new byte[readAheadBufferSize], new byte[readAheadBufferSize], new LineDescriptor[16])];
        byte[] outputStagingBuffer = new byte[64];
        using MemoryStream output = new();

        await KWayMerge.MergeAsync(
            [RunOf(lines)], cursorBuffers, output, outputStagingBuffer, maxLineLength,
            ct: TestContext.Current.CancellationToken);

        byte[] expected = Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line + "\n")));
        Assert.Equal(expected, output.ToArray());
    }

    [Fact]
    [Trait("Case", "KM-18")]
    public async Task Merging_issues_one_read_per_window_plus_one_that_finds_end_of_stream()
    {
        const int lineCount = 20;
        const int linesPerWindow = 5;
        const int bytesPerLine = 9;
        const int windowSize = linesPerWindow * bytesPerLine;
        const int descriptorCapacity = 20; // above linesPerWindow, so bytes, not descriptors, end each window

        string[] expected = [.. Enumerable.Range(0, lineCount).Select(i => $"{i:D5}. X")];
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(expected.Select(l => l + "\n")));
        TrackingStream run = new(data);

        RunCursorBuffers[] cursorBuffers =
        [
            new RunCursorBuffers(new byte[windowSize], new byte[windowSize], new LineDescriptor[descriptorCapacity]),
        ];
        using MemoryStream output = new();
        byte[] outputStagingBuffer = new byte[128];

        await KWayMerge.MergeAsync(
            [run], cursorBuffers, output, outputStagingBuffer, maxLineLength: bytesPerLine - 1,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(expected, ReadLines(output));
        Assert.Equal(lineCount / linesPerWindow + 1, run.ReadCount);
    }

    [Fact]
    [Trait("Case", "KM-16")]
    public async Task A_middle_cursors_construction_failure_still_disposes_every_stream()
    {
        DisposeTrackingStream runA = new(Encoding.UTF8.GetBytes("1. Apple\n"));
        DisposeTrackingStream runB = new(Encoding.UTF8.GetBytes("2. Banana\n"));
        DisposeTrackingStream runC = new(Encoding.UTF8.GetBytes("3. Cherry\n"));

        static RunCursorBuffers Valid() => new(new byte[128], new byte[128], new LineDescriptor[8]);

        RunCursorBuffers invalid = new(new byte[4], new byte[4], new LineDescriptor[8]);
        RunCursorBuffers[] cursorBuffers = [Valid(), invalid, Valid()];

        using MemoryStream output = new();
        byte[] outputStagingBuffer = new byte[128];

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => KWayMerge.MergeAsync(
            [runA, runB, runC], cursorBuffers, output, outputStagingBuffer, maxLineLength: 127,
            ct: TestContext.Current.CancellationToken));

        Assert.True(runA.Disposed);
        Assert.True(runB.Disposed);
        Assert.True(runC.Disposed);
    }

    [Fact]
    [Trait("Case", "KM-27")]
    public async Task Extra_buffer_sets_go_unused_while_too_few_are_rejected_disposing_every_stream()
    {
        static RunCursorBuffers Buffers() => new(new byte[128], new byte[128], new LineDescriptor[8]);
        byte[] outputStagingBuffer = new byte[128];

        using (MemoryStream output = new())
        {
            await KWayMerge.MergeAsync(
                [RunOf("1. Apple"), RunOf("2. Banana")], [Buffers(), Buffers(), Buffers()], output, outputStagingBuffer,
                maxLineLength: 126, ct: TestContext.Current.CancellationToken);
            Assert.Equal(["1. Apple", "2. Banana"], ReadLines(output));
        }

        DisposeTrackingStream runA = new(Encoding.UTF8.GetBytes("1. Apple\n"));
        DisposeTrackingStream runB = new(Encoding.UTF8.GetBytes("2. Banana\n"));
        using MemoryStream unused = new();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => KWayMerge.MergeAsync(
            [runA, runB], [Buffers()], unused, outputStagingBuffer, maxLineLength: 126,
            ct: TestContext.Current.CancellationToken));

        Assert.True(runA.Disposed);
        Assert.True(runB.Disposed);
    }

    private static async Task<List<string>> MergeAsync(IReadOnlyList<Stream> runs, int bufferSize = 128, int maxLineLength = 126)
    {
        RunCursorBuffers[] cursorBuffers = [.. Enumerable.Range(0, runs.Count)
            .Select(_ => new RunCursorBuffers(new byte[bufferSize], new byte[bufferSize], new LineDescriptor[32]))];
        using MemoryStream output = new();
        byte[] outputStagingBuffer = new byte[bufferSize];

        await KWayMerge.MergeAsync(
            runs, cursorBuffers, output, outputStagingBuffer, maxLineLength,
            ct: TestContext.Current.CancellationToken);

        return ReadLines(output);
    }

    private static List<string> ReadLines(MemoryStream output)
    {
        string text = Encoding.UTF8.GetString(output.ToArray());
        if (text.Length == 0)
        {
            return [];
        }

        string[] parts = text.Split('\n');
        return [.. parts[..^1]];
    }

    private static MemoryStream RunOf(params string[] lines) =>
        new(Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line + "\n"))));

    private static TrackingStream TrackingRunOf(string[] lines) =>
        new(Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line + "\n"))));

    private sealed class TrackingStream(byte[] data) : MemoryStream(data)
    {
        public int MaxRequestedRead { get; private set; }
        public int ReadCount { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaxRequestedRead = Math.Max(MaxRequestedRead, buffer.Length);
            ReadCount++;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class DisposeTrackingStream(byte[] data) : MemoryStream(data)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            return base.DisposeAsync();
        }
    }
}
