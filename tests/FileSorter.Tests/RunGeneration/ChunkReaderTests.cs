using System.Text;
using FileSorter.LineFormat;
using FileSorter.RunGeneration;
using Xunit;

namespace FileSorter.Tests.RunGeneration;

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
        byte[] data = "1. Apple\nnotaline\n"u8.ToArray();
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 4096, descriptorCapacity: 16, capacity: 2);
        ChunkReader reader = new(input, pool, maxLineLength: 1024);

        await Assert.ThrowsAsync<MalformedLineException>(
            () => reader.ReadNextAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(0, pool.Outstanding);
        PooledBuffer buffer = await pool.AcquireAsync(TestContext.Current.CancellationToken);
        buffer.Dispose();

        await reader.DisposeAsync();
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task Reading_releases_its_slot_when_the_final_unterminated_line_is_malformed()
    {
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
        using MemoryStream input = new([]);
        BufferPool poolBelowFloor = new(bufferSize: 66, descriptorCapacity: 8, capacity: 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkReader(input, poolBelowFloor, maxLineLength: 64));
    }

    [Fact]
    public void A_pool_of_max_line_plus_three_bytes_is_the_smallest_accepted()
    {
        using MemoryStream input = new([]);
        BufferPool poolAtFloor = new(bufferSize: 67, descriptorCapacity: 8, capacity: 1);

        _ = new ChunkReader(input, poolAtFloor, maxLineLength: 64);
    }

    [Fact]
    public void A_pool_with_no_room_to_describe_a_line_is_rejected()
    {
        using MemoryStream input = new([]);
        BufferPool pool = new(bufferSize: 64, descriptorCapacity: 0, capacity: 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkReader(input, pool, maxLineLength: 32));
    }

    [Fact]
    public async Task Reading_a_longest_possible_final_line_emits_it_rather_than_looping()
    {
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
        // 5 lines * 5 bytes: line 6 starts at byte 25.
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
        byte[] data = Encoding.UTF8.GetBytes("1. A\n2. B\n3. C\n4. D\n5. E\nnotaline\n");
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 30, descriptorCapacity: 4, capacity: 2);
        ChunkReader reader = new(input, pool, maxLineLength: 8);

        Chunk? first = await reader.ReadNextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, first!.Value.Count);
        first.Value.Buffer.Dispose();

        MalformedLineException failure = await Assert.ThrowsAsync<MalformedLineException>(
            () => reader.ReadNextAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(25, failure.ByteOffset);
        Assert.Equal(6, failure.LineNumber);
        Assert.Equal(0, pool.Outstanding);

        await reader.DisposeAsync();
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task A_byte_order_mark_is_stripped_only_where_the_file_actually_starts()
    {
        // The literals hold an invisible U+FEFF at the start and before "Banana".
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
        // Reserve 8 + 2 = 10, fill 33 - 10 = 23: the first fill ends on line 3's CR (byte 22).
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
        // Reserve 10, fill 23; carry 11 leaves room for 33 - 11 = 22 fresh bytes, so 1 goes to _pending.
        // Kept 22 < fill 23 is not EOF.
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
        // Reserve 10, fill 24; only 23 bytes remain but carry 12 leaves room for 22, so the last byte goes to _pending.
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
        // Capacity 1: the prefetch acquire blocks until cancelled.
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, 20).Select(i => $"{i}. Line {i}\n")));
        using MemoryStream input = new(data);
        BufferPool pool = new(bufferSize: 40, descriptorCapacity: 4, capacity: 1);
        ChunkReader reader = new(input, pool, maxLineLength: 16);
        using CancellationTokenSource cts = new();

        Task<Chunk?> readTask = reader.ReadNextAsync(cts.Token).AsTask();

        // MemoryStream reads complete synchronously, so 50 ms is ample to reach the blocked acquire.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);

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
