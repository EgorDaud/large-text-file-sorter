using System.Text;
using FileSorter.LineFormat;
using FileSorter.RunGeneration;
using Xunit;

namespace FileSorter.Tests.RunGeneration;

// ChunkReader issues the next slot's fill before parsing the current one, folding an
// ordinary carry into the head room Reserve (maxLineLength + 2) leaves for it and
// rebuilding a slot from scratch on a carry that overflows Reserve. Several cases
// below exist only to exercise that split: the Reserve boundary itself, the overflow
// rebuild with and without a pending spill, and that a cancelled or failed prefetch
// leaves Outstanding at zero once the reader is disposed. Buffer sizes are chosen
// well above the floor except where the floor itself is the point; a size at the
// floor shrinks the fresh fill region past Reserve to a handful of bytes.
public sealed class ChunkReaderTests
{
    [Fact]
    public async Task Reading_a_stream_that_fits_in_one_fill_produces_one_chunk_then_exhausts()
    {
        byte[] data = "1. Apple\n2. Banana\n3. Cherry\n"u8.ToArray();
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 4096, descriptorCapacity: 16, capacity: 2);
        ChunkReader reader = new(input, pool, maxLineLength: 1024);

        Chunk? chunk = await reader.ReadNextAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(chunk);
        Assert.Equal(3, chunk!.Value.Count);
        Assert.Equal(["1. Apple", "2. Banana", "3. Cherry"], ReadBack(chunk.Value));
        chunk.Value.Buffer.Dispose();

        Assert.Null(await reader.ReadNextAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(data.Length, reader.BytesConsumed);
        Assert.Equal(3, reader.LinesRead);
    }

    [Fact]
    public async Task Reading_input_with_no_trailing_terminator_still_emits_the_final_line()
    {
        // LineCursor never reports an unterminated tail as a line, because it cannot
        // tell a genuine end of file from a read that merely stopped mid-line.
        // ChunkReader can tell, and owns finalising the trailing fragment once the
        // stream is truly spent.
        byte[] data = "1. Apple\n2. Banana"u8.ToArray();
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 4096, descriptorCapacity: 16, capacity: 2);
        ChunkReader reader = new(input, pool, maxLineLength: 1024);

        Chunk? chunk = await reader.ReadNextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, chunk!.Value.Count);
        Assert.Equal(["1. Apple", "2. Banana"], ReadBack(chunk.Value));
        chunk.Value.Buffer.Dispose();

        Assert.Null(await reader.ReadNextAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task Reading_carries_partial_lines_across_many_small_fills()
    {
        // The pool buffer is far smaller than the input, forcing dozens of
        // acquire-fill-carry cycles: the case that would show a carry copied to the
        // wrong head position, or a fill request sized against the whole buffer
        // instead of against the room left past Reserve.
        string[] expected = [.. Enumerable.Range(0, 50).Select(i => $"{i}. Line number {i}")];
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(expected.Select(line => line + "\n")));
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 64, descriptorCapacity: 4, capacity: 2);
        ChunkReader reader = new(input, pool, maxLineLength: 28);

        List<string> lines = [];
        while (await reader.ReadNextAsync(TestContext.Current.CancellationToken) is { } chunk)
        {
            lines.AddRange(ReadBack(chunk));
            chunk.Buffer.Dispose();
        }

        Assert.Equal(expected, lines);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(50, reader.LinesRead);
    }

    [Fact]
    public async Task Reading_a_final_unterminated_line_ending_in_a_bare_carriage_return_strips_it()
    {
        // A bare, unterminated trailing carriage return is the first half of a "\r\n"
        // terminator whose line feed never arrived, not a content byte, so it is
        // stripped the same way a "\r" immediately before a line feed is. The claim
        // is only about a line with no embedded "\r" of its own: a line whose content
        // genuinely ends in "\r" cannot be told apart from this one.
        byte[] data = "1. Apple\r"u8.ToArray();
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 4096, descriptorCapacity: 16, capacity: 2);
        ChunkReader reader = new(input, pool, maxLineLength: 1024);

        Chunk? chunk = await reader.ReadNextAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(chunk);
        Assert.Equal(1, chunk!.Value.Count);
        Assert.Equal(["1. Apple"], ReadBack(chunk.Value));
        chunk.Value.Buffer.Dispose();
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task Reading_a_file_that_is_only_a_bare_carriage_return_produces_no_lines()
    {
        // The degenerate case of the same rule: when the whole unterminated tail is
        // that one byte, there is no content left to describe at all.
        byte[] data = "\r"u8.ToArray();
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 4096, descriptorCapacity: 16, capacity: 2);
        ChunkReader reader = new(input, pool, maxLineLength: 1024);

        Chunk? chunk = await reader.ReadNextAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(chunk);
        Assert.Equal(0, chunk!.Value.Count);
        chunk.Value.Buffer.Dispose();
        Assert.Equal(0, reader.LinesRead);
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task Reading_ends_a_chunk_when_descriptors_exhaust_even_though_bytes_remain()
    {
        // A chunk ends when either resource runs out. Four lines comfortably fit the
        // byte buffer but not the two-descriptor capacity, so the boundary falls on
        // the descriptor limit, and the two lines left undescribed must reappear,
        // intact, as the very next chunk rather than being dropped.
        byte[] data = "1. A\n2. B\n3. C\n4. D\n"u8.ToArray();
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 4096, descriptorCapacity: 2, capacity: 2);
        ChunkReader reader = new(input, pool, maxLineLength: 1024);

        Chunk? first = await reader.ReadNextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, first!.Value.Count);
        Assert.Equal(["1. A", "2. B"], ReadBack(first.Value));
        first.Value.Buffer.Dispose();

        Chunk? second = await reader.ReadNextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, second!.Value.Count);
        Assert.Equal(["3. C", "4. D"], ReadBack(second.Value));
        second.Value.Buffer.Dispose();

        Assert.Null(await reader.ReadNextAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reading_releases_its_slot_when_a_line_is_malformed()
    {
        // A malformed line is a guaranteed path on bad input, not an edge case, so
        // the slot this reader never hands on is released right here. Capacity 2, not
        // 1: the reader acquires the second slot and issues its fill before parsing
        // the first, so even a single-chunk input needs room for two slots at once --
        // the second is what the parse's throw releases through
        // ReleaseUnusedPrefetchAsync rather than through the fold.
        byte[] data = "1. Apple\nnotaline\n"u8.ToArray();
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 4096, descriptorCapacity: 16, capacity: 2);
        ChunkReader reader = new(input, pool, maxLineLength: 1024);

        await Assert.ThrowsAsync<MalformedLineException>(
            () => reader.ReadNextAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(0, pool.Outstanding);
        PooledBuffer buffer = await pool.AcquireAsync(TestContext.Current.CancellationToken);
        buffer.Dispose();

        // DisposeAsync must be harmless here: the throw already released everything
        // the reader was holding -- the chunk being parsed and the second slot whose
        // fill was in flight -- so dispose must observe nothing pending and release
        // nothing a second time.
        await reader.DisposeAsync();
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task Reading_releases_its_slot_when_the_final_unterminated_line_is_malformed()
    {
        // The same release discipline applies whether the failure comes from the
        // ordinary line-by-line loop or from finalising a trailing fragment at true
        // end of stream: both routes call the same parser and share the one catch.
        // Capacity 2, not 1, because of the prefetched second slot.
        byte[] data = "1. Apple\nnotaline"u8.ToArray();
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 4096, descriptorCapacity: 16, capacity: 2);
        ChunkReader reader = new(input, pool, maxLineLength: 1024);

        await Assert.ThrowsAsync<MalformedLineException>(
            () => reader.ReadNextAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task Reading_empty_input_returns_null_immediately_and_releases_its_slot()
    {
        using MemoryStream input = new([]);
        BufferPool pool = new(bufferSize: 4096, descriptorCapacity: 16, capacity: 1);
        ChunkReader reader = new(input, pool, maxLineLength: 1024);

        Assert.Null(await reader.ReadNextAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, pool.Outstanding);

        await reader.DisposeAsync();
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public void A_pool_of_max_line_plus_two_bytes_leaves_no_room_for_a_fresh_byte_and_is_rejected()
    {
        // Every fill's fresh bytes land past Reserve, a fixed region sized before the
        // carry ahead of it is known, so the buffer has to leave a fresh byte of room
        // past THAT, not merely past whatever carry a fill happens to observe.
        // Reserve is maxLineLength + 2 because a legal carry can be maxLineLength + 1
        // bytes: one maximum-length line plus a trailing CR, when a fill lands exactly
        // on it. A buffer of maxLineLength + 2 therefore leaves such a carry no room
        // for a fresh byte and is rejected.
        using MemoryStream input = new([]);
        BufferPool poolBelowFloor = new(bufferSize: 66, descriptorCapacity: 8, capacity: 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkReader(input, poolBelowFloor, maxLineLength: 64));
    }

    [Fact]
    public void A_pool_of_max_line_plus_three_bytes_is_the_smallest_accepted()
    {
        using MemoryStream input = new([]);
        BufferPool poolAtFloor = new(bufferSize: 67, descriptorCapacity: 8, capacity: 1);

        // maxLineLength + 3 is the floor, and is what MemoryBudget hands out. There is
        // no assertion because the claim is exactly that construction completes
        // without throwing; asserting NotNull on a fresh reference would prove nothing.
        _ = new ChunkReader(input, poolAtFloor, maxLineLength: 64);
    }

    [Fact]
    public void A_pool_with_no_room_to_describe_a_line_is_rejected()
    {
        // With no descriptor capacity no line could ever be described, so reading
        // would return empty chunks for ever; the constructor rejects the pool
        // instead of leaving the caller to spin.
        using MemoryStream input = new([]);
        BufferPool pool = new(bufferSize: 64, descriptorCapacity: 0, capacity: 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkReader(input, pool, maxLineLength: 32));
    }

    [Fact]
    public async Task Reading_a_longest_possible_final_line_emits_it_rather_than_looping()
    {
        // The line is exactly maxLineLength and has no terminator, so the fresh-fill
        // region comes back short of what it asked for -- that shortfall is what
        // tells the reader the stream is exhausted and the tail can be finalised
        // rather than carried again.
        byte[] data = Encoding.UTF8.GetBytes("1. " + new string('A', 60));
        Assert.Equal(63, data.Length);
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 128, descriptorCapacity: 8, capacity: 2);
        ChunkReader reader = new(input, pool, maxLineLength: 63);

        List<string> lines = [];
        while (await reader.ReadNextAsync(TestContext.Current.CancellationToken) is { } chunk)
        {
            lines.AddRange(ReadBack(chunk));
            chunk.Buffer.Dispose();
        }

        Assert.Equal([Encoding.UTF8.GetString(data)], lines);
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task Reading_a_line_longer_than_a_whole_buffer_fails_rather_than_looping()
    {
        // Longer than a whole buffer, so no fill can ever complete it: the cursor's
        // own limit catches this, and it must fail rather than carry for ever.
        // Capacity 2, not 1, because the second slot's fill is issued before this
        // chunk is parsed.
        byte[] data = Encoding.UTF8.GetBytes("1. " + new string('A', 200) + "\n");
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 64, descriptorCapacity: 8, capacity: 2);
        ChunkReader reader = new(input, pool, maxLineLength: 30);

        await Assert.ThrowsAsync<MalformedLineException>(
            () => reader.ReadNextAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    [Trait("Case", "LP-25")]
    public async Task A_malformed_line_reports_its_offset_line_number_and_a_preview_truncated_to_the_documented_length()
    {
        // The diagnostic is assembled here rather than in LineParser, which reports only
        // that a line is malformed and knows neither where it sat nor which line it was.
        // The preview length is what is untested elsewhere and what this case exists for:
        // five slices of the sorter each carry their own PreviewMaxBytes = 128, and an
        // untruncated preview turns one hostile line into a 64 KiB stderr message. Every
        // well-formed line here is five bytes, so line 6 starts at byte 25.
        const int previewMaxBytes = 128;
        string offending = "notaline" + new string('X', 300);
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("1. A\n", 5)) + offending + "\n");
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 4096, descriptorCapacity: 16, capacity: 2);
        ChunkReader reader = new(input, pool, maxLineLength: 1024); // above the line, so the length check does not fire first

        MalformedLineException failure = await Assert.ThrowsAsync<MalformedLineException>(
            () => reader.ReadNextAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(25, failure.ByteOffset);
        Assert.Equal(6, failure.LineNumber);
        Assert.Equal(offending[..previewMaxBytes], failure.Preview);
        Assert.Contains("25", failure.Message);
        Assert.Contains("line 6", failure.Message);
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task A_malformed_line_beyond_the_first_chunk_reports_its_file_offset_and_line_number()
    {
        // The exception has to point an operator at the offending bytes in the file,
        // which an offset into whichever buffer happened to be in hand cannot do on a
        // file read in thousands of them. Chunk 1 stops on descriptor capacity after
        // four lines with a one-byte carry ("5", the start of line 5's number), so
        // chunk 2's span begins at spanStart 8; line 5 sits at span offset 0 ("5"
        // folded from the carry plus ". E\n" from the fresh fill), and the malformed
        // "notaline" after it starts at span offset 5. Absolute file coordinates make
        // that 25, not the block-relative figure.
        byte[] data = Encoding.UTF8.GetBytes("1. A\n2. B\n3. C\n4. D\n5. E\nnotaline\n");
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 30, descriptorCapacity: 4, capacity: 2);
        ChunkReader reader = new(input, pool, maxLineLength: 8);

        Chunk? first = await reader.ReadNextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, first!.Value.Count); // descriptor capacity, not bytes, ends the first chunk
        first.Value.Buffer.Dispose();

        MalformedLineException failure = await Assert.ThrowsAsync<MalformedLineException>(
            () => reader.ReadNextAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(25, failure.ByteOffset);
        Assert.Equal(6, failure.LineNumber);
        Assert.Equal(0, pool.Outstanding);

        // No prefetch survives the throw: ReadNextAsync's catch releases the
        // speculative fill through ReleaseUnusedPrefetchDuringUnwindAsync, since the
        // parse that would otherwise fold it never got that far.
        await reader.DisposeAsync();
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task A_byte_order_mark_is_stripped_only_where_the_file_actually_starts()
    {
        // 0xEF 0xBB 0xBF is U+FEFF, an ordinary character wherever it is not the
        // file's first three bytes. A cursor built per chunk with no idea where it
        // sits would strip it from any chunk that happened to begin with it, silently
        // shortening a line whose bytes are otherwise compared as they are. The
        // buffer is small enough, relative to the data, to force two chunks.
        byte[] data = Encoding.UTF8.GetBytes("﻿1. Apple\n2. ﻿Banana\n");
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 40, descriptorCapacity: 4, capacity: 2);
        ChunkReader reader = new(input, pool, maxLineLength: 16);

        List<string> lines = [];
        while (await reader.ReadNextAsync(TestContext.Current.CancellationToken) is { } chunk)
        {
            lines.AddRange(ReadBack(chunk));
            chunk.Buffer.Dispose();
        }

        Assert.Equal(["1. Apple", "2. ﻿Banana"], lines);
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task A_fill_boundary_landing_on_the_cr_of_a_maximum_length_crlf_line_carries_it_intact()
    {
        // maxLineLength 8, Reserve 10 (maxLineLength + 2), so the fill amount is 23.
        // The third line's content ("1. abcde") is exactly maxLineLength, and the
        // first 23-byte fill lands exactly on its CR (positions 0-22), one byte short
        // of its LF: a maximum-length line split at its CR must carry whole rather
        // than be rejected as over-length. descriptorCapacity 8 is generous, so every
        // chunk boundary here falls on bytes running out and the ordinary fold path
        // is the one under test.
        byte[] data = "1. abc\n1. abc\n1. abcde\r\n1. z\n"u8.ToArray();
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 33, descriptorCapacity: 8, capacity: 2);
        ChunkReader reader = new(input, pool, maxLineLength: 8);

        List<string> lines = [];
        while (await reader.ReadNextAsync(TestContext.Current.CancellationToken) is { } chunk)
        {
            lines.AddRange(ReadBack(chunk));
            chunk.Buffer.Dispose();
        }

        Assert.Equal(["1. abc", "1. abc", "1. abcde", "1. z"], lines);
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task A_fill_boundary_landing_on_the_cr_of_a_maximum_length_crlf_line_carries_it_intact_through_the_overflow_rebuild_path()
    {
        // The same shape as above, but descriptorCapacity 1 moves every chunk boundary
        // onto the other resource: each chunk stops after a single described line,
        // well before bytes run out, so the leftover repeatedly overflows Reserve and
        // the rebuild branch of the fold carries the CR-terminated fragment through
        // several rebuilds rather than one ordinary fold.
        byte[] data = "1. abc\n1. abc\n1. abcde\r\n1. z\n"u8.ToArray();
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 33, descriptorCapacity: 1, capacity: 2);
        ChunkReader reader = new(input, pool, maxLineLength: 8);

        List<string> lines = [];
        while (await reader.ReadNextAsync(TestContext.Current.CancellationToken) is { } chunk)
        {
            lines.AddRange(ReadBack(chunk));
            chunk.Buffer.Dispose();
        }

        Assert.Equal(["1. abc", "1. abc", "1. abcde", "1. z"], lines);
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task Reading_folds_an_oversized_carry_into_the_next_slots_head_without_a_pending_spill()
    {
        // descriptorCapacity 1 stops every chunk on capacity rather than on bytes, so
        // the leftover is several whole undescribed lines -- far past Reserve
        // (maxLineLength + 2 = 4) -- which is the fold's overflow rebuild branch. The
        // input is short enough that the overflow fill's own read comes back small,
        // so the rebuild happens in place without spilling into _pending.
        string[] expected = [.. Enumerable.Range(1, 4).Select(i => $"{i}.")];
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(expected.Select(line => line + "\n")));
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 13, descriptorCapacity: 1, capacity: 2);
        ChunkReader reader = new(input, pool, maxLineLength: 2);

        List<string> lines = [];
        while (await reader.ReadNextAsync(TestContext.Current.CancellationToken) is { } chunk)
        {
            lines.AddRange(ReadBack(chunk));
            chunk.Buffer.Dispose();
        }

        Assert.Equal(expected, lines);
        Assert.Equal(4, reader.LinesRead);
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task Reading_spills_the_unkept_remainder_of_an_oversized_carry_into_pending()
    {
        // The same overflow branch, forced this time to spill into _pending: 200
        // identical short lines behind a tiny maxLineLength (Reserve is 4) and a
        // bufferSize far larger than Reserve but small next to the whole input, so
        // descriptor capacity 1 produces a carry that claims most of a slot while the
        // fill already in flight for that slot brings back more than fits alongside
        // it.
        string[] expected = [.. Enumerable.Repeat("1.", 200)];
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(expected.Select(line => line + "\n")));
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 12, descriptorCapacity: 1, capacity: 2);
        ChunkReader reader = new(input, pool, maxLineLength: 2);

        List<string> lines = [];
        while (await reader.ReadNextAsync(TestContext.Current.CancellationToken) is { } chunk)
        {
            lines.AddRange(ReadBack(chunk));
            chunk.Buffer.Dispose();
        }

        Assert.Equal(expected, lines);
        Assert.Equal(200, reader.LinesRead);
        Assert.Equal(data.Length, reader.BytesConsumed);
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task A_rebuilt_slots_own_short_fill_does_not_fold_its_tail_when_the_shortfall_is_only_the_carrys_own_room()
    {
        // maxLineLength 8 => Reserve 10, bufferSize 33 => fill amount 23. Four short
        // lines exhaust descriptorCapacity 4 in the first chunk and leave an 11-byte
        // carry (> Reserve), so the fold rebuilds the next slot: of the second fill's
        // 23 fresh bytes, 22 are kept in the rebuilt slot and 1 spills into _pending.
        // The rebuilt slot therefore holds three whole lines plus an unterminated
        // tail whose continuation is the byte in _pending -- the stream is NOT
        // exhausted. The trap: a rebuilt slot's kept count (22) is short of
        // _fillAmount (23) because the carry claimed the room, not because the stream
        // ended, so a reader comparing the two folds that tail in as a false final
        // line ("1. abc" instead of "1. abcde"). _prefetchCountIsCapped is what keeps
        // the two apart.
        string[] expected = [.. Enumerable.Repeat("1.", 4), .. Enumerable.Repeat("1. abcde", 6)];
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(expected.Select(line => line + "\n")));
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 33, descriptorCapacity: 4, capacity: 2);
        ChunkReader reader = new(input, pool, maxLineLength: 8);

        List<string> lines = [];
        while (await reader.ReadNextAsync(TestContext.Current.CancellationToken) is { } chunk)
        {
            lines.AddRange(ReadBack(chunk));
            chunk.Buffer.Dispose();
        }

        Assert.Equal(expected, lines);
        Assert.Equal(data.Length, reader.BytesConsumed);
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task A_rebuilt_slot_still_does_not_fold_when_its_own_short_fill_is_a_genuine_short_read_because_pending_still_holds_the_true_tail()
    {
        // The same overflow rebuild, but the rebuild's speculative fill -- in flight
        // before the oversized carry that forces the rebuild is known -- comes back
        // short of _fillAmount (24) for a genuine reason as well: the stream has only
        // 23 bytes left. Those 23 bytes still overflow the rebuilt slot's room (22,
        // once the 12-byte carry claims its head), so the file's last byte spills into
        // _pending. Exhaustion is still not genuine at this chunk, so it must not fold
        // its trailing tail ("1. abcd") in as a final line; the call after it draws
        // that one byte from _pending with nothing behind it in the stream, and folds
        // the reassembled "1. abcde" as the true final line.
        string[] expected = ["1.", "1.", "1.", "1.", "1. abcde", "1. abcde", "1. abcde", "1. abcde"];
        byte[] data = Encoding.UTF8.GetBytes("1.\n1.\n1.\n1.\n1. abcde\n1. abcde\n1. abcde\n1. abcde");
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 34, descriptorCapacity: 4, capacity: 2);
        ChunkReader reader = new(input, pool, maxLineLength: 8);

        List<string> lines = [];
        while (await reader.ReadNextAsync(TestContext.Current.CancellationToken) is { } chunk)
        {
            lines.AddRange(ReadBack(chunk));
            chunk.Buffer.Dispose();
        }

        Assert.Equal(expected, lines);
        Assert.Equal(8, reader.LinesRead);
        Assert.Equal(data.Length, reader.BytesConsumed);
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task A_real_failure_from_the_final_chunks_unneeded_speculative_fill_still_surfaces()
    {
        // The speculative fill for the slot after the true last chunk is issued before
        // that chunk is known to be the last one; it ordinarily resolves to 0 bytes at
        // EOF and is released silently. A genuine IOException from that same fill is
        // not EOF and must not be swallowed with it, or the sort would succeed having
        // read less than the whole input. The stream below returns real data once,
        // then 0 (letting the last chunk's own fill end normally and short), then
        // throws on every read after that -- which is where the speculative fill
        // lands.
        byte[] data = "1. Apple\n"u8.ToArray();
        using ThrowsAfterOneEofRead input = new(data);
        BufferPool pool = new(bufferSize: 64, descriptorCapacity: 4, capacity: 2);
        ChunkReader reader = new(input, pool, maxLineLength: 32);

        await Assert.ThrowsAsync<IOException>(
            () => reader.ReadNextAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task Cancellation_during_a_prefetch_still_releases_the_chunk_it_was_issued_from()
    {
        // A pool of exactly one slot means the prefetch's acquire, issued before the
        // first chunk is parsed, can never succeed while the caller still holds the
        // chunk already delivered. Cancelling is how this test resolves that stall,
        // the same way a genuine shutdown would.
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, 20).Select(i => $"{i}. Line {i}\n")));
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 40, descriptorCapacity: 4, capacity: 1);
        ChunkReader reader = new(input, pool, maxLineLength: 16);
        using CancellationTokenSource cts = new();

        Task<Chunk?> readTask = reader.ReadNextAsync(cts.Token).AsTask();

        // Long enough for the call to reach the blocked prefetch acquire -- the first
        // chunk's fill and parse await nothing a MemoryStream cannot satisfy
        // synchronously -- and short enough not to slow the suite.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);

        // ReadNextAsync's catch releases the chunk this call was about to return; the
        // cancelled acquire never got a slot of its own to leak.
        Assert.Equal(0, pool.Outstanding);

        await reader.DisposeAsync();
        Assert.Equal(0, pool.Outstanding);
    }

    private static string[] ReadBack(Chunk chunk)
    {
        string[] result = new string[chunk.Count];
        for (int i = 0; i < chunk.Count; i++)
        {
            LineDescriptor line = chunk.Buffer.Lines[i];
            result[i] = Encoding.UTF8.GetString(chunk.Buffer.Bytes, line.Offset, line.Length);
        }

        return result;
    }

    // Reads its data normally, reports EOF exactly once -- so the last chunk's own
    // fill ends normally and short -- and throws IOException on every read after
    // that, standing in for a disk that fails on the speculative next-slot fill.
    private sealed class ThrowsAfterOneEofRead(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        private int _eofReads;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_inner.Position >= _inner.Length)
            {
                if (_eofReads++ > 0)
                {
                    throw new IOException("simulated read failure past end of stream");
                }

                return ValueTask.FromResult(0);
            }

            return _inner.ReadAsync(buffer, cancellationToken);
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
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
