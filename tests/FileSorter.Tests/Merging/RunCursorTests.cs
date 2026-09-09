using System.Text;
using FileSorter.LineFormat;
using FileSorter.Merging;
using Xunit;

namespace FileSorter.Tests.Merging;

// RunCursor's requirement is that a cursor over a stream yields every line in order and
// reports exhaustion exactly once, the phase-two counterpart of what ChunkReaderTests
// covers over the same LineCursor. The double-buffered read-ahead adds a second
// requirement these tests also cover: the fill for the window not currently being
// delivered runs concurrently with that delivery, never disturbs the buffer still being
// read from, and is safely observed before the stream underneath it is disposed.
public sealed class RunCursorTests
{
    [Fact]
    public async Task A_cursor_yields_every_line_in_order()
    {
        byte[] data = "1. Apple\n2. Banana\n3. Cherry\n"u8.ToArray();
        await using MemoryStream run = new(data);
        RunCursor cursor = new(run, Buffers(65, 65, 16), maxLineLength: 63);

        List<string> lines = [];
        while (await cursor.MoveNextAsync(TestContext.Current.CancellationToken))
        {
            lines.Add(ReadCurrent(cursor));
        }

        Assert.Equal(["1. Apple", "2. Banana", "3. Cherry"], lines);
    }

    [Fact]
    public async Task Exhaustion_is_reported_exactly_once_and_stays_false_on_further_calls()
    {
        byte[] data = "1. Apple\n"u8.ToArray();
        await using MemoryStream run = new(data);
        RunCursor cursor = new(run, Buffers(65, 65, 16), maxLineLength: 63);

        Assert.True(await cursor.MoveNextAsync(TestContext.Current.CancellationToken));
        Assert.False(await cursor.MoveNextAsync(TestContext.Current.CancellationToken));
        Assert.False(await cursor.MoveNextAsync(TestContext.Current.CancellationToken)); // stays false, not an error
    }

    [Fact]
    public async Task An_empty_run_reports_exhaustion_immediately()
    {
        await using MemoryStream run = new([]);
        RunCursor cursor = new(run, Buffers(65, 65, 16), maxLineLength: 63);

        Assert.False(await cursor.MoveNextAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_final_line_with_no_trailing_terminator_is_still_yielded()
    {
        byte[] data = "1. Apple\n2. Banana"u8.ToArray();
        await using MemoryStream run = new(data);
        RunCursor cursor = new(run, Buffers(65, 65, 16), maxLineLength: 63);

        List<string> lines = [];
        while (await cursor.MoveNextAsync(TestContext.Current.CancellationToken))
        {
            lines.Add(ReadCurrent(cursor));
        }

        Assert.Equal(["1. Apple", "2. Banana"], lines);
    }

    [Fact]
    public async Task A_final_unterminated_line_ending_in_a_bare_carriage_return_strips_it()
    {
        // A run file this sorter wrote always ends in '\n' and never reaches this
        // branch in production, but the cursor applies the rule regardless of a
        // stream's provenance, the same as ChunkReader: a bare trailing '\r' is the
        // first half of a terminator whose '\n' never arrived, not a content byte.
        byte[] data = "1. Apple\r"u8.ToArray();
        await using MemoryStream run = new(data);
        RunCursor cursor = new(run, Buffers(65, 65, 16), maxLineLength: 63);

        List<string> lines = [];
        while (await cursor.MoveNextAsync(TestContext.Current.CancellationToken))
        {
            lines.Add(ReadCurrent(cursor));
        }

        Assert.Equal(["1. Apple"], lines);
    }

    [Fact]
    public async Task A_run_that_is_only_a_bare_carriage_return_yields_no_lines()
    {
        // The degenerate case the same rule implies: a lone unterminated '\r' with
        // nothing before it is an empty tail, not a one-byte line, the same as a
        // bare trailing "\r\n" leaves no residual line.
        byte[] data = "\r"u8.ToArray();
        await using MemoryStream run = new(data);
        RunCursor cursor = new(run, Buffers(65, 65, 16), maxLineLength: 63);

        Assert.False(await cursor.MoveNextAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_run_file_beginning_with_a_byte_order_mark_is_delivered_with_the_bytes_intact()
    {
        // LineCursor's mark-stripping rule is written for the user's input file, not
        // for a run file this sorter wrote itself, so RunCursor passes
        // stripByteOrderMark: false and these three bytes are ordinary content, never
        // consumed. The parse failure landing at byte offset 0 -- exactly where the
        // mark's first byte sits -- is what proves it, rather than the mark being
        // silently stripped and the line parsing as if it were absent.
        byte[] data = [0xEF, 0xBB, 0xBF, .. "1. Apple\n"u8.ToArray()];
        await using MemoryStream run = new(data);
        RunCursor cursor = new(run, Buffers(65, 65, 8), maxLineLength: 63);

        MalformedLineException ex = await Assert.ThrowsAsync<MalformedLineException>(
            () => cursor.MoveNextAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(0, ex.ByteOffset);
    }

    [Fact]
    public async Task A_run_line_ending_in_a_carriage_return_before_the_line_feed_keeps_it_as_content()
    {
        // A run file this sorter wrote is terminated by a bare '\n' throughout, so a
        // '\r' immediately before one is always content the sorter carried in -- from
        // an input line read as "5. a\r\r\n", content "5. a\r" -- never the
        // terminator's second half. RunCursor passes stripCarriageReturn: false, so
        // this line delivers "5. a\r" intact; stripping it a second time here would
        // silently lose that byte.
        byte[] data = "5. a\r\n"u8.ToArray();
        await using MemoryStream run = new(data);
        RunCursor cursor = new(run, Buffers(65, 65, 16), maxLineLength: 63);

        Assert.True(await cursor.MoveNextAsync(TestContext.Current.CancellationToken));
        Assert.Equal("5. a\r", ReadCurrent(cursor));
        Assert.False(await cursor.MoveNextAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_read_ahead_buffer_far_smaller_than_the_run_still_yields_every_line_in_order()
    {
        // Forces dozens of fill-then-carry cycles within a single window — the case
        // that would show a carry shift overwriting unread bytes instead of moving
        // them to the front of the same buffer. The 32-byte window holds only one or
        // two lines at a time, so every boundary here is the bytes running out — the
        // 16-slot descriptor array never fills first — and every window still yields
        // at least one complete line, so every boundary is a prefetch swap
        // (IssuePrefetch), never the same-buffer resume ShiftAndFillAsync otherwise
        // handles. The case below contrasts the other resource: a buffer that holds the
        // whole run, with windows ending on descriptor capacity instead of bytes.
        string[] expected = [.. Enumerable.Range(0, 60).Select(i => $"{i}. Line number {i}")];
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(expected.Select(line => line + "\n")));
        await using MemoryStream run = new(data);
        RunCursor cursor = new(run, Buffers(32, 32, 16), maxLineLength: 28);

        List<string> lines = [];
        while (await cursor.MoveNextAsync(TestContext.Current.CancellationToken))
        {
            lines.Add(ReadCurrent(cursor));
        }

        Assert.Equal(expected, lines);
    }

    [Fact]
    public async Task A_descriptor_array_smaller_than_the_window_still_yields_every_line_in_order()
    {
        // A window ends when either resource runs out; here the read-ahead buffer holds
        // the whole run comfortably, so the only thing that can end a window is the
        // descriptor array filling. Everything past the last described line has to come
        // back as carry-over rather than be mistaken for the end of the stream, which is
        // the defect that would silently truncate a run mid-merge.
        //
        // Every window here takes the same-buffer resume path (ShiftAndFillAsync), not
        // IssuePrefetch's swap, and the window holding the whole run is why: the very
        // first fill reads every byte the stream has, which is fewer than the window
        // asked for, so _streamExhausted is set on that first fill and no prefetch is
        // ever issued. The sibling case below, whose window is smaller than the run and
        // whose first fill is therefore not short, is the one that exercises the swap.
        string[] expected = [.. Enumerable.Range(0, 40).Select(i => $"{i}. L{i:D3}")];
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(expected.Select(line => line + '\n')));
        await using MemoryStream run = new(data);
        RunCursor cursor = new(run, Buffers(512, 512, 3), maxLineLength: 64);

        List<string> lines = [];
        while (await cursor.MoveNextAsync(TestContext.Current.CancellationToken))
        {
            lines.Add(ReadCurrent(cursor));
        }

        Assert.Equal(expected, lines);
    }

    [Fact]
    public async Task A_window_that_ends_on_descriptor_capacity_carries_its_tail_correctly_across_several_windows()
    {
        // The double-buffer-specific case: descriptor capacity forces a window to end
        // mid-buffer (most of the window is still undelivered carry, not a lone partial
        // line), that carry is copied into the
        // FRONT of the other buffer, and the rest of that buffer is freshly filled --
        // all before this window's own descriptors are handed out. The read-ahead
        // buffer here comfortably holds the whole run, so every window boundary but
        // the last is forced by the descriptor array filling at exactly 4 lines,
        // which over 50 lines forces at least twelve such windows -- comfortably past
        // the "at least three" this case asks for. Getting every one of those swaps
        // wrong in the same way (losing or duplicating bytes at the boundary) is
        // exactly what would still let the final count come out right while
        // scrambling the order, so both are asserted.
        const int descriptorCapacity = 4;
        const int lineCount = 50;
        string[] expected = [.. Enumerable.Range(0, lineCount).Select(i => $"{i}. Item{i:D3}")];
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(expected.Select(line => line + '\n')));
        await using MemoryStream run = new(data);
        RunCursor cursor = new(run, Buffers(256, 256, descriptorCapacity), maxLineLength: 64);

        List<string> lines = [];
        while (await cursor.MoveNextAsync(TestContext.Current.CancellationToken))
        {
            lines.Add(ReadCurrent(cursor));
        }

        Assert.Equal(expected, lines); // no line lost, none delivered twice, none reordered
    }

    [Fact]
    public void A_read_ahead_buffer_that_cannot_hold_a_longest_line_and_its_terminator_is_rejected()
    {
        // The phase-two half of the invariant ChunkReader states for phase one. Sized
        // merely equal to the limit, the buffer can fill edge to edge with carry and
        // leave no room to read the byte that would end the line -- so AdvanceAsync
        // could never tell "this line is longer than the buffer" apart from "the
        // stream is exhausted" without reading further, and a full buffer has nowhere
        // further to read into. Applies to either buffer, since a fill can land in
        // either one.
        using MemoryStream run = new([]);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RunCursor(run, Buffers(64, 64, 8), maxLineLength: 64));
    }

    [Fact]
    public void A_window_of_max_line_plus_one_bytes_leaves_no_room_for_a_fresh_byte_and_is_rejected()
    {
        // LineCursor's no-newline branch can legally hand back a carry of
        // maxLineLength + 1 bytes -- content plus a trailing CR whose LF has not
        // arrived, as in the CRLF-at-the-boundary case above -- so a window of
        // maxLineLength + 1 leaves that carry no room for a fresh byte and is
        // rejected.
        using MemoryStream run = new([]);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RunCursor(run, Buffers(65, 65, 8), maxLineLength: 64));
    }

    [Fact]
    public void A_window_of_max_line_plus_two_bytes_is_the_smallest_accepted()
    {
        using MemoryStream run = new([]);

        // Does not throw: this is MemoryBudget's own floor, maxLineLength + 2.
        _ = new RunCursor(run, Buffers(66, 66, 8), maxLineLength: 64);
    }

    [Fact]
    public void A_second_buffer_alone_below_the_limit_is_also_rejected()
    {
        using MemoryStream run = new([]);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RunCursor(run, Buffers(128, 64, 8), maxLineLength: 64));
    }

    [Fact]
    public void A_cursor_whose_two_read_ahead_buffers_differ_in_size_is_rejected()
    {
        // MemoryPlan.BytesPerRunCursor charges 2 x ReadAheadBufferSize on the
        // assumption that both windows are the same size; a cursor built on two
        // different sizes would make that budget figure describe an allocation nobody
        // made.
        using MemoryStream run = new([]);

        Assert.Throws<ArgumentException>(
            () => new RunCursor(run, Buffers(96, 80, 8), maxLineLength: 64));
    }

    [Fact]
    public void A_cursor_with_nowhere_to_put_a_descriptor_is_rejected()
    {
        // The same non-terminating loop by way of the other resource: no window can ever
        // deliver a line, so no window ever ends.
        using MemoryStream run = new([]);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RunCursor(run, new RunCursorBuffers(new byte[64], new byte[64], []), maxLineLength: 32));
    }

    [Fact]
    public async Task A_longest_possible_line_is_yielded_rather_than_spinning()
    {
        // Exactly maxLineLength, so it fills the buffer to within two bytes of the edge
        // and leaves its terminator for the following fill -- two, not one, because
        // this line carries no CR. The window's floor (maxLineLength + 2) is calibrated
        // against the largest carry LineCursor can EVER hand back, which is
        // maxLineLength + 1 and only when a \r\n line's CR lands at the fill boundary
        // (the case below); a bare LF-only line's carry tops out at maxLineLength.
        string longest = "1. " + new string('A', 60);
        Assert.Equal(63, Encoding.UTF8.GetByteCount(longest));
        byte[] data = Encoding.UTF8.GetBytes(longest + "\n2. Banana\n");
        await using MemoryStream run = new(data);
        RunCursor cursor = new(run, Buffers(65, 65, 8), maxLineLength: 63);

        List<string> lines = [];
        while (await cursor.MoveNextAsync(TestContext.Current.CancellationToken))
        {
            lines.Add(ReadCurrent(cursor));
        }

        Assert.Equal([longest, "2. Banana"], lines);
    }

    [Fact]
    public async Task A_window_that_ends_on_the_cr_of_a_maximum_length_line_delivers_it_intact()
    {
        // A maximum-length line whose last content byte is a '\r' landing exactly on a
        // window boundary: maxLineLength 9, window 14 (comfortably above the floor of
        // maxLineLength + 2). The first window's fill is completely full -- "1. A\n"
        // (5 bytes) plus "1. abcde\r" (9 bytes, content exactly maxLineLength, its last
        // byte a genuine content '\r' under the run-file rule, not a terminator's first
        // half) -- landing on that CR with the run's real terminating LF one byte
        // beyond it, unread. That carry of maxLineLength bytes is what the window floor
        // has to hold alongside at least one fresh byte; the LF arrives as the other
        // window's first fresh byte, completing the line intact with its content '\r'
        // retained.
        byte[] data = "1. A\n1. abcde\r\n2. z\n"u8.ToArray();
        await using MemoryStream run = new(data);
        RunCursor cursor = new(run, Buffers(14, 14, 8), maxLineLength: 9);

        List<string> lines = [];
        while (await cursor.MoveNextAsync(TestContext.Current.CancellationToken))
        {
            lines.Add(ReadCurrent(cursor));
        }

        Assert.Equal(["1. A", "1. abcde\r", "2. z"], lines);
    }

    [Fact]
    public async Task A_line_past_the_maximum_length_is_reported_as_malformed_rather_than_read_ahead()
    {
        // One byte past maxLineLength, present whole in the first (and only) fill:
        // LineCursor's own over-length check throws before RunCursor ever gets a
        // chance to treat this as ordinary carry-over.
        string tooLong = "1. " + new string('A', 61);
        byte[] data = Encoding.UTF8.GetBytes(tooLong + "\n");
        await using MemoryStream run = new(data);
        RunCursor cursor = new(run, Buffers(128, 128, 8), maxLineLength: 63);

        await Assert.ThrowsAsync<MalformedLineException>(
            () => cursor.MoveNextAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task A_malformed_line_several_windows_in_is_reported_at_its_own_file_offset_and_line_number()
    {
        // Three well-formed lines, each its own window (10-byte buffers, 5 bytes per
        // line, matching the window-boundary shape The_next_windows_fill_... below
        // uses), so the malformed fourth line is only reached after at least one
        // prefetch swap has already happened. "garbage" has no '.' in it at all, so
        // LineParser.TryParse fails on the separator search rather than the number
        // parse -- either way RunCursor.Describe is what throws, not LineCursor.
        string[] goodLines = ["1. A", "2. B", "3. C"];
        string badLine = "garbage";
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(goodLines.Select(l => l + "\n")) + badLine + "\n");
        await using MemoryStream run = new(data);
        RunCursor cursor = new(run, Buffers(10, 10, 2), maxLineLength: 8);

        CancellationToken ct = TestContext.Current.CancellationToken;
        for (int i = 0; i < goodLines.Length; i++)
        {
            Assert.True(await cursor.MoveNextAsync(ct));
        }

        MalformedLineException ex = await Assert.ThrowsAsync<MalformedLineException>(
            () => cursor.MoveNextAsync(ct).AsTask());

        long expectedOffset = goodLines.Sum(l => l.Length + 1); // bytes of the three good lines and their terminators
        Assert.Equal(expectedOffset, ex.ByteOffset);
        Assert.Equal(4, ex.LineNumber);
    }

    [Fact]
    public async Task The_next_windows_fill_is_issued_before_the_current_windows_last_descriptor_is_consumed()
    {
        // RunCursor issues the read for the OTHER window as soon as this one has been
        // scanned, not lazily once this one's descriptors run out -- the whole point of
        // the double buffer. Six lines at two lines (exactly ten bytes) per
        // window force two clean window boundaries, each landing exactly on a buffer
        // boundary so neither fill needs a second, short ReadAsync call of its own --
        // which keeps "the second read" unambiguous in the recorded order below.
        string[] lines = ["1. A", "2. B", "3. C", "4. D", "5. E", "6. F"];
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n"))); // 5 bytes each, 30 total
        await using OrderRecordingStream run = new(data);
        await using RunCursor cursor = new(run, Buffers(10, 10, 2), maxLineLength: 8);

        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.True(await cursor.MoveNextAsync(ct)); // scans window 1 (reads[0]) and, before returning, issues reads[1] for window 2
        run.Events.Add("consumed:" + ReadCurrent(cursor)); // "1. A"
        Assert.True(await cursor.MoveNextAsync(ct)); // delivers window 1's last descriptor from what is already in memory
        run.Events.Add("consumed:" + ReadCurrent(cursor)); // "2. B", the last of window 1

        int secondReadIndex = run.Events.IndexOf("read-2");
        int lastOfWindowOneIndex = run.Events.IndexOf("consumed:2. B");

        Assert.True(secondReadIndex >= 0);
        Assert.True(lastOfWindowOneIndex >= 0);
        Assert.True(
            secondReadIndex < lastOfWindowOneIndex,
            $"Expected window 2's read to be issued before window 1's last descriptor was consumed. Order: {string.Join(", ", run.Events)}");
    }

    [Fact]
    public async Task Disposing_while_a_prefetch_fill_is_pending_waits_for_it_and_does_not_throw()
    {
        // The invariant RunCursor.DisposeAsync itself protects: the stream must not
        // be torn down under a read still in flight against it. GatedStream holds the
        // second ReadAsync call -- the prefetch for window 2, issued while window 1's
        // two lines are still being handed out -- open until the test releases it.
        string[] lines = ["1. A", "2. B", "3. C", "4. D"];
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n"))); // 5 bytes each, 20 total
        GatedStream run = new(data, gatedReadNumber: 2);
        RunCursor cursor = new(run, Buffers(10, 10, 2), maxLineLength: 8);

        CancellationToken ct = TestContext.Current.CancellationToken;
        await cursor.MoveNextAsync(ct); // scans window 1 and issues the gated prefetch for window 2

        ValueTask disposeTask = cursor.DisposeAsync();
        await Task.Delay(50, ct);
        Assert.False(disposeTask.IsCompleted); // must not race ahead of the read it is waiting on

        run.ReleaseGate();
        await disposeTask; // completes once the read does, and does not throw
    }

    [Fact]
    public async Task Disposing_while_a_prefetch_fill_has_faulted_swallows_the_fault()
    {
        // The other half of the same invariant: DisposeAsync observes the prefetch
        // task so the stream is never disposed under a read still nominally in
        // flight, but nobody is left to act on what that read produced -- including
        // a genuine I/O failure. FaultingStream's second ReadAsync (the prefetch for
        // window 2) throws before this test's disposal ever gets a chance to await it.
        string[] lines = ["1. A", "2. B", "3. C", "4. D"];
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));
        FaultingStream run = new(data, faultingReadNumber: 2);
        RunCursor cursor = new(run, Buffers(10, 10, 2), maxLineLength: 8);

        await cursor.MoveNextAsync(TestContext.Current.CancellationToken); // scans window 1, issues the faulting prefetch

        await cursor.DisposeAsync(); // must not throw despite the prefetch having failed with IOException
    }

    [Fact]
    public async Task Cancellation_while_a_prefetch_is_pending_surfaces_from_MoveNextAsync()
    {
        // The prefetch is awaited, not merely started, once this window's descriptors
        // run out -- CancelableStream's second ReadAsync (the prefetch for window 2)
        // hangs until cancelled, so this proves that await genuinely observes the
        // token rather than a fill that already raced to completion.
        string[] lines = ["1. A", "2. B", "3. C", "4. D"];
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));
        CancelableStream run = new(data);
        RunCursor cursor = new(run, Buffers(10, 10, 2), maxLineLength: 8);

        using CancellationTokenSource cts = new();
        Assert.True(await cursor.MoveNextAsync(cts.Token)); // scans window 1, issues the hanging prefetch for window 2
        Assert.True(await cursor.MoveNextAsync(cts.Token)); // delivers window 1's last descriptor without touching the prefetch

        Task<bool> thirdCall = cursor.MoveNextAsync(cts.Token).AsTask(); // now awaits the hung prefetch
        await Task.Delay(20, CancellationToken.None);
        Assert.False(thirdCall.IsCompleted);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => thirdCall);
    }

    [Fact]
    public async Task TryMoveNext_delivers_every_pending_descriptor_and_returns_false_at_the_windows_end()
    {
        // TryMoveNext must hand out every descriptor a window already holds without
        // touching the stream, then report false exactly when the window is exhausted;
        // MoveNextAsync, not TryMoveNext, is what refills. Six lines at two lines (ten bytes)
        // per window, the same shape as the prefetch-ordering test above, gives one
        // window boundary to cross partway through.
        string[] lines = ["1. A", "2. B", "3. C", "4. D", "5. E", "6. F"];
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));
        await using MemoryStream run = new(data);
        RunCursor cursor = new(run, Buffers(10, 10, 2), maxLineLength: 8);

        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.True(await cursor.MoveNextAsync(ct)); // fills window 1, delivers its first descriptor
        Assert.Equal("1. A", ReadCurrent(cursor));

        Assert.True(cursor.TryMoveNext()); // window 1's remaining descriptor, no fill needed
        Assert.Equal("2. B", ReadCurrent(cursor));

        Assert.False(cursor.TryMoveNext()); // window 1 exhausted -- exactly at its end, not before

        Assert.True(await cursor.MoveNextAsync(ct)); // only MoveNextAsync refills, for window 2
        Assert.Equal("3. C", ReadCurrent(cursor));
    }

    [Fact]
    public void TryMoveNext_on_a_fresh_cursor_returns_false_without_issuing_a_read()
    {
        // Before any window has ever been scanned, _pendingCount is still its zero
        // default, so TryMoveNext must report false from that alone -- never by
        // starting or waiting on a fill. OrderRecordingStream (see the prefetch-
        // ordering test above) records the instant a read is issued, so an empty
        // Events list is direct evidence none ever was.
        byte[] data = "1. Apple\n"u8.ToArray();
        using OrderRecordingStream run = new(data);
        RunCursor cursor = new(run, Buffers(65, 65, 16), maxLineLength: 63);

        Assert.False(cursor.TryMoveNext());
        Assert.Empty(run.Events);
    }

    [Fact]
    public async Task Every_delivered_descriptors_bytes_match_its_line_under_genuinely_asynchronous_reads()
    {
        // AsyncDelayStream's ReadAsync genuinely completes on the thread pool after a
        // short delay rather than synchronously, which is what gives a prefetch racing
        // the buffer still being delivered from any real chance to interleave. A small
        // descriptor array against 300 lines forces dozens of window boundaries, and
        // reading each descriptor's bytes out of Buffer the instant it is delivered
        // -- not after the loop finishes -- is what would catch a prefetch fill
        // landing in the buffer still being read from.
        string[] expected = [.. Enumerable.Range(0, 300).Select(i => $"{i}. Item{i:D4}")];
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(expected.Select(l => l + "\n")));
        await using AsyncDelayStream run = new(data);
        RunCursor cursor = new(run, Buffers(64, 64, 3), maxLineLength: 32);

        List<string> lines = [];
        CancellationToken ct = TestContext.Current.CancellationToken;
        while (await cursor.MoveNextAsync(ct))
        {
            lines.Add(ReadCurrent(cursor));
        }

        Assert.Equal(expected, lines);
    }

    private static RunCursorBuffers Buffers(int firstWindowSize, int secondWindowSize, int descriptorCapacity) =>
        new(new byte[firstWindowSize], new byte[secondWindowSize], new LineDescriptor[descriptorCapacity]);

    private static string ReadCurrent(RunCursor cursor)
    {
        LineDescriptor current = cursor.Current;
        return Encoding.UTF8.GetString(cursor.Buffer, current.Offset, current.Length);
    }

    // Records "read-N" the instant the Nth ReadAsync call is made against this
    // stream, before it (or the base implementation) does anything further --
    // which is what lets a test assert that a read was *issued* by a certain point,
    // not merely that it eventually completed.
    private sealed class OrderRecordingStream(byte[] data) : MemoryStream(data)
    {
        public List<string> Events { get; } = [];
        private int _readCount;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Events.Add($"read-{++_readCount}");
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    // The Nth ReadAsync call against this stream does not complete until the test
    // calls ReleaseGate, so a caller awaiting it observes a fill genuinely still in
    // flight rather than one that merely raced to finish first.
    private sealed class GatedStream(byte[] data, int gatedReadNumber) : MemoryStream(data)
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;

        public void ReleaseGate() => _gate.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++_readCount == gatedReadNumber)
            {
                await _gate.Task;
            }

            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    // The Nth ReadAsync call against this stream throws immediately, standing in for
    // a read that fails against real media (a device error, a network share dropping)
    // rather than one that merely returns short at end of stream.
    private sealed class FaultingStream(byte[] data, int faultingReadNumber) : MemoryStream(data)
    {
        private int _readCount;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++_readCount == faultingReadNumber)
            {
                throw new IOException("Simulated read failure.");
            }

            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    // The Nth ReadAsync call against this stream hangs until the token passed to it
    // is cancelled, standing in for a real device read a caller genuinely needs to
    // cancel rather than one that merely completes before anybody asks.
    private sealed class CancelableStream(byte[] data) : MemoryStream(data)
    {
        private int _readCount;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++_readCount == 2)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    // Every ReadAsync call against this stream genuinely yields to the thread pool
    // before completing, unlike MemoryStream's own synchronous ReadAsync -- the
    // difference that gives a fill racing a delivery any real chance to interleave.
    private sealed class AsyncDelayStream(byte[] data) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }
}
