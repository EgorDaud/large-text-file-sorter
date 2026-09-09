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
        // The placement shortcut and the planner both keep a lone run away from the
        // merge in production, but the merge itself must not depend on that.
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
        // Ten runs, each contributing lines throughout the range, so the winner
        // changes run on almost every emission and most replays climb past a
        // single level of the loser tree rather than resolving at the leaf.
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
        // The zero-padded string part sorts identically to the underlying integer
        // it encodes, so the expected order is just that integer set sorted — not
        // the lines' own textual order, which the leading number part would upset.
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
        Assert.Equal(["5. Apple", "5. Apple", "5. Apple"], result[..3]); // adjacent, none lost
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

        // "007. Apple" and "7. Apple" tie on string part and number, so only the raw
        // bytes decide, and '0' sorts below '7'.
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
        // k = 3 rather than the k = 2 pairing the tie cases above use: the tied pair's
        // leaf replay through LoserTree.Replay has to climb past a third, never-tying
        // leaf on the way to the root, not merely resolve a single two-leaf match.
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
        // A double-advance drops a line; a failure to advance duplicates one. Every
        // line here is distinct, so either defect changes the exact multiset the
        // assertion below checks, not merely the count.
        List<string> result = await MergeAsync(
        [
            RunOf("1. A", "4. D", "7. G"),
            RunOf("2. B", "5. E", "8. H"),
            RunOf("3. C", "6. F", "9. I"),
        ]);

        Assert.Equal(9, result.Count); // sum of the three input counts
        Assert.Equal(["1. A", "2. B", "3. C", "4. D", "5. E", "6. F", "7. G", "8. H", "9. I"], result);
    }

    [Fact]
    [Trait("Case", "KM-11")]
    public async Task Merging_a_large_run_set_never_requests_a_read_larger_than_the_read_ahead_buffer()
    {
        // The memory claim for phase two: if the merge ever materialised a whole input,
        // some single read would have to ask for far more than one buffer's worth of
        // bytes. Tracking the largest single read request catches that structurally,
        // without measuring process memory.
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

        // r + i * runCount, for r across the run count and i across each run's own
        // length, covers 0..(runCount * linesPerRun - 1) exactly once — so the
        // expected order is simply that range, ascending.
        string[] expected = [.. Enumerable.Range(0, runCount * linesPerRun).Select(v => $"{v}. L{v:D7}")];
        Assert.Equal(expected, result);

        Assert.All(runs, run => Assert.True(run.MaxRequestedRead <= bufferSize));
    }

    [Fact]
    [Trait("Case", "KM-15")]
    public async Task Lines_longer_than_the_staging_buffer_are_written_directly_mid_run_and_at_the_end()
    {
        // A line longer than the output staging buffer is written straight through
        // rather than staged. OutputBufferSize (1 MiB) exceeds the default --max-line
        // (64 KiB), so the stock configuration does not take this path, but --max-line
        // tuned above the default, or a small enough --memory, reaches it routinely.
        // The 64-byte staging buffer here stands in for that relationship at a testable
        // size: the read-ahead buffer is large enough to hold these lines, since
        // LineCursor's limit is independent of the staging buffer, but the staging
        // buffer is not. One over-length line follows two lines that have already
        // partly filled the buffer; a second is the very last line the run produces.
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
        // This pins how many reads a merge issues against the underlying stream: one
        // per window, plus one more that discovers end of stream once a final window
        // is prefetched and comes back empty. Fixed-width lines (9 bytes including the
        // terminator) and a 45-byte window make every window hold exactly 5 lines with
        // nothing carried over, so bytes end every window well before the generous
        // 20-descriptor capacity could; the run's 20 lines are therefore exactly 4
        // windows, so 5 reads total. RunCursor's synchronous fast path does not change
        // this count -- it saves an async state machine on the common case, which is a
        // CPU property the benchmarks measure, not a read count.
        const int lineCount = 20;
        const int linesPerWindow = 5;
        const int bytesPerLine = 9; // 5-digit number, ". ", "X", '\n'
        const int windowSize = linesPerWindow * bytesPerLine;
        const int descriptorCapacity = 20; // generous: bytes end every window, not this

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
        // MergeAsync owns every stream handed to it from the moment it is called, so
        // cursor construction happens inside its try: a RunCursor constructor throwing
        // partway through the loop otherwise leaks every earlier cursor's stream and
        // every later run's raw stream, which a finally that only disposes already-built
        // cursors never reaches. The middle run here gets buffers far below the
        // maxLineLength + 2 floor, so its RunCursor throws with one cursor already built
        // ahead of it (owning runA's stream) and one run behind it never wrapped in a
        // cursor at all (runC's raw stream). All three must still end up disposed.
        DisposeTrackingStream runA = new(Encoding.UTF8.GetBytes("1. Apple\n"));
        DisposeTrackingStream runB = new(Encoding.UTF8.GetBytes("2. Banana\n"));
        DisposeTrackingStream runC = new(Encoding.UTF8.GetBytes("3. Cherry\n"));

        static RunCursorBuffers Valid() => new(new byte[128], new byte[128], new LineDescriptor[8]);

        // A window of 4 bytes is far below maxLineLength (127) + 2: RunCursor's own
        // constructor guard rejects it before this call ever touches the stream.
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

        // Every emitted line, including the last, carries its own terminator, so
        // splitting on it always leaves exactly one trailing empty entry to drop.
        string[] parts = text.Split('\n');
        return [.. parts[..^1]];
    }

    private static MemoryStream RunOf(params string[] lines) =>
        new(Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line + "\n"))));

    private static TrackingStream TrackingRunOf(string[] lines) =>
        new(Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line + "\n"))));

    // Records the largest single ReadAsync request RunCursor issues against this run --
    // a merge that buffers a whole input shows up here as a read the size of the input
    // itself -- and how many ReadAsync calls actually reach the stream, where a call
    // per line rather than per window shows up as a count near the line count.
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

    // Records whether this stream was ever disposed, through either disposal path --
    // MergeAsync's own cleanup can reach a stream through RunCursor.DisposeAsync (a
    // stream already wrapped in a cursor) or by disposing it directly (a stream a
    // failed construction never got to wrap), and this test needs to tell that both
    // routes were actually taken, not merely that the object still exists.
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
