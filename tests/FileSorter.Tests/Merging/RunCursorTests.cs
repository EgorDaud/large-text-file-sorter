using System.Text;
using FileSorter.LineFormat;
using FileSorter.Merging;
using Xunit;

namespace FileSorter.Tests.Merging;

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
        Assert.False(await cursor.MoveNextAsync(TestContext.Current.CancellationToken));
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
        byte[] data = "\r"u8.ToArray();
        await using MemoryStream run = new(data);
        RunCursor cursor = new(run, Buffers(65, 65, 16), maxLineLength: 63);

        Assert.False(await cursor.MoveNextAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_run_file_beginning_with_a_byte_order_mark_is_delivered_with_the_bytes_intact()
    {
        // A stripped mark would parse cleanly; failing at offset 0 proves the bytes stayed.
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
        // 32-byte windows run out of bytes before 16 descriptors fill, so every boundary is a prefetch swap.
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
        // The first fill reads the whole run, so no prefetch is issued and every window resumes in place.
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

        Assert.Equal(expected, lines);
    }

    [Fact]
    public void A_read_ahead_buffer_that_cannot_hold_a_longest_line_and_its_terminator_is_rejected()
    {
        using MemoryStream run = new([]);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RunCursor(run, Buffers(64, 64, 8), maxLineLength: 64));
    }

    [Fact]
    public void A_window_of_max_line_plus_one_bytes_leaves_no_room_for_a_fresh_byte_and_is_rejected()
    {
        // LineCursor may carry maxLineLength + 1 bytes: a full line plus a CR whose LF has not arrived.
        using MemoryStream run = new([]);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RunCursor(run, Buffers(65, 65, 8), maxLineLength: 64));
    }

    [Fact]
    public void A_window_of_max_line_plus_two_bytes_is_the_smallest_accepted()
    {
        using MemoryStream run = new([]);

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
        using MemoryStream run = new([]);

        Assert.Throws<ArgumentException>(
            () => new RunCursor(run, Buffers(96, 80, 8), maxLineLength: 64));
    }

    [Fact]
    public void A_cursor_with_nowhere_to_put_a_descriptor_is_rejected()
    {
        using MemoryStream run = new([]);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RunCursor(run, new RunCursorBuffers(new byte[64], new byte[64], []), maxLineLength: 32));
    }

    [Fact]
    public async Task A_longest_possible_line_is_yielded_rather_than_spinning()
    {
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
        // The first 14-byte fill ends on the content '\r'; its LF is the next window's first byte.
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

        long expectedOffset = goodLines.Sum(l => l.Length + 1);
        Assert.Equal(expectedOffset, ex.ByteOffset);
        Assert.Equal(4, ex.LineNumber);
    }

    [Fact]
    public async Task The_next_windows_fill_is_issued_before_the_current_windows_last_descriptor_is_consumed()
    {
        // Each 10-byte window holds exactly two lines, so every fill is one ReadAsync and read-2 is window 2's.
        string[] lines = ["1. A", "2. B", "3. C", "4. D", "5. E", "6. F"];
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));
        await using OrderRecordingStream run = new(data);
        await using RunCursor cursor = new(run, Buffers(10, 10, 2), maxLineLength: 8);

        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.True(await cursor.MoveNextAsync(ct));
        run.Events.Add("consumed:" + ReadCurrent(cursor));
        Assert.True(await cursor.MoveNextAsync(ct));
        run.Events.Add("consumed:" + ReadCurrent(cursor));

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
        string[] lines = ["1. A", "2. B", "3. C", "4. D"];
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));
        GatedStream run = new(data, gatedReadNumber: 2);
        RunCursor cursor = new(run, Buffers(10, 10, 2), maxLineLength: 8);

        CancellationToken ct = TestContext.Current.CancellationToken;
        await cursor.MoveNextAsync(ct);

        ValueTask disposeTask = cursor.DisposeAsync();
        await Task.Delay(50, ct);
        Assert.False(disposeTask.IsCompleted);

        run.ReleaseGate();
        await disposeTask;
    }

    [Fact]
    public async Task Disposing_while_a_prefetch_fill_has_faulted_swallows_the_fault()
    {
        string[] lines = ["1. A", "2. B", "3. C", "4. D"];
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));
        FaultingStream run = new(data, faultingReadNumber: 2);
        RunCursor cursor = new(run, Buffers(10, 10, 2), maxLineLength: 8);

        await cursor.MoveNextAsync(TestContext.Current.CancellationToken);

        await cursor.DisposeAsync();
    }

    [Fact]
    public async Task Cancellation_while_a_prefetch_is_pending_surfaces_from_MoveNextAsync()
    {
        string[] lines = ["1. A", "2. B", "3. C", "4. D"];
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));
        CancelableStream run = new(data);
        RunCursor cursor = new(run, Buffers(10, 10, 2), maxLineLength: 8);

        using CancellationTokenSource cts = new();
        Assert.True(await cursor.MoveNextAsync(cts.Token));
        Assert.True(await cursor.MoveNextAsync(cts.Token));

        Task<bool> thirdCall = cursor.MoveNextAsync(cts.Token).AsTask();
        await Task.Delay(20, CancellationToken.None);
        Assert.False(thirdCall.IsCompleted);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => thirdCall);
    }

    [Fact]
    public async Task TryMoveNext_delivers_every_pending_descriptor_and_returns_false_at_the_windows_end()
    {
        string[] lines = ["1. A", "2. B", "3. C", "4. D", "5. E", "6. F"];
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));
        await using MemoryStream run = new(data);
        RunCursor cursor = new(run, Buffers(10, 10, 2), maxLineLength: 8);

        CancellationToken ct = TestContext.Current.CancellationToken;
        Assert.True(await cursor.MoveNextAsync(ct));
        Assert.Equal("1. A", ReadCurrent(cursor));

        Assert.True(cursor.TryMoveNext());
        Assert.Equal("2. B", ReadCurrent(cursor));

        Assert.False(cursor.TryMoveNext());

        Assert.True(await cursor.MoveNextAsync(ct));
        Assert.Equal("3. C", ReadCurrent(cursor));
    }

    [Fact]
    public void TryMoveNext_on_a_fresh_cursor_returns_false_without_issuing_a_read()
    {
        byte[] data = "1. Apple\n"u8.ToArray();
        using OrderRecordingStream run = new(data);
        RunCursor cursor = new(run, Buffers(65, 65, 16), maxLineLength: 63);

        Assert.False(cursor.TryMoveNext());
        Assert.Empty(run.Events);
    }

    [Fact]
    public async Task Every_delivered_descriptors_bytes_match_its_line_under_genuinely_asynchronous_reads()
    {
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

    // MemoryStream.ReadAsync completes synchronously; yielding lets a prefetch really race delivery.
    private sealed class AsyncDelayStream(byte[] data) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }
}
