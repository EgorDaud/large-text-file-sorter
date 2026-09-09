using System.Text;
using FileSorter.LineFormat;
using Xunit;

namespace FileSorter.Tests.LineFormat;

public sealed class LineCursorTests
{
    private const int DefaultMaxLineLength = 64 * 1024;

    [Fact]
    [Trait("Case", "CB-01")]
    public void Reading_a_block_that_ends_exactly_on_a_terminator_leaves_no_carry_over()
    {
        byte[] block = "1. Apple\n2. Banana\n"u8.ToArray();
        LineCursor cursor = new(block, DefaultMaxLineLength);

        Assert.True(cursor.TryReadLine(out int offsetA, out int lengthA));
        Assert.Equal("1. Apple", Encoding.UTF8.GetString(block, offsetA, lengthA));
        Assert.True(cursor.TryReadLine(out int offsetB, out int lengthB));
        Assert.Equal("2. Banana", Encoding.UTF8.GetString(block, offsetB, lengthB));
        Assert.False(cursor.TryReadLine(out _, out _));
        Assert.Equal(0, cursor.CarryLength);
    }

    [Fact]
    [Trait("Case", "CB-02")]
    public void Reading_a_block_that_ends_mid_line_carries_the_partial_tail_into_the_next_read()
    {
        List<string> lines = ReadAcrossBlocks(
            ["1. Apple\n2. Ban"u8.ToArray(), "ana\n3. Cherry\n"u8.ToArray()],
            DefaultMaxLineLength,
            out int finalCarryLength);

        Assert.Equal(["1. Apple", "2. Banana", "3. Cherry"], lines);
        Assert.Equal(0, finalCarryLength);
    }

    [Fact]
    [Trait("Case", "CB-03")]
    public void Reading_a_line_spanning_more_than_two_reads_emits_it_once_intact()
    {
        // Within the limit throughout: each partial carry stays under maxLineLength,
        // which is the property this case exists to pin, not merely the final result.
        const int maxLineLength = 40;
        string longString = new('X', 30);
        byte[] whole = Encoding.UTF8.GetBytes($"9. {longString}\n");

        byte[][] blocks = [whole[..10], whole[10..20], whole[20..]];
        List<string> lines = ReadAcrossBlocks(blocks, maxLineLength, out int finalCarryLength, maxObservedCarry =>
            Assert.True(maxObservedCarry <= maxLineLength));

        Assert.Equal([$"9. {longString}"], lines);
        Assert.Equal(0, finalCarryLength);
    }

    [Fact]
    [Trait("Case", "CB-04")]
    public void Reading_a_final_block_with_no_trailing_terminator_still_emits_the_last_line()
    {
        byte[] block = "1. Apple\n2. Banana"u8.ToArray();
        LineCursor cursor = new(block, DefaultMaxLineLength);

        Assert.True(cursor.TryReadLine(out int offsetA, out int lengthA));
        Assert.Equal("1. Apple", Encoding.UTF8.GetString(block, offsetA, lengthA));
        Assert.False(cursor.TryReadLine(out _, out _));
        Assert.Equal("2. Banana", Encoding.UTF8.GetString(block, cursor.CarryOffset, cursor.CarryLength));
    }

    [Fact]
    [Trait("Case", "CB-05")]
    public void Reading_past_the_final_terminator_treats_the_trailing_region_as_nothing()
    {
        byte[] block = "1. Apple\n"u8.ToArray();
        LineCursor cursor = new(block, DefaultMaxLineLength);

        Assert.True(cursor.TryReadLine(out int offset, out int length));
        Assert.Equal("1. Apple", Encoding.UTF8.GetString(block, offset, length));
        Assert.False(cursor.TryReadLine(out _, out _));
        Assert.Equal(0, cursor.CarryLength);
    }

    [Fact]
    [Trait("Case", "CB-06")]
    public void Reading_a_block_using_only_the_single_character_terminator_finds_correct_boundaries()
    {
        byte[] block = "1. Apple\n2. Banana\n"u8.ToArray();
        LineCursor cursor = new(block, DefaultMaxLineLength);

        Assert.True(cursor.TryReadLine(out int offset, out int length));
        Assert.Equal("1. Apple", Encoding.UTF8.GetString(block, offset, length));
    }

    [Fact]
    [Trait("Case", "CB-07")]
    public void Reading_a_block_using_the_two_character_convention_strips_the_carriage_return()
    {
        byte[] block = "1. Apple\r\n2. Banana\r\n"u8.ToArray();
        LineCursor cursor = new(block, DefaultMaxLineLength);

        Assert.True(cursor.TryReadLine(out int offsetA, out int lengthA));
        Assert.Equal("1. Apple", Encoding.UTF8.GetString(block, offsetA, lengthA));
        Assert.True(cursor.TryReadLine(out int offsetB, out int lengthB));
        Assert.Equal("2. Banana", Encoding.UTF8.GetString(block, offsetB, lengthB));
        Assert.False(cursor.TryReadLine(out _, out _));
    }

    [Fact]
    [Trait("Case", "CB-08")]
    public void Reading_a_two_character_terminator_split_across_a_read_boundary_finds_one_boundary()
    {
        List<string> lines = ReadAcrossBlocks(
            ["1. Apple\r"u8.ToArray(), "\n2. Banana\r\n"u8.ToArray()],
            DefaultMaxLineLength,
            out int finalCarryLength);

        Assert.Equal(["1. Apple", "2. Banana"], lines);
        Assert.Equal(0, finalCarryLength);
    }

    [Fact]
    [Trait("Case", "CB-09")]
    public void Reading_a_block_mixing_both_terminator_conventions_finds_every_boundary()
    {
        byte[] block = "1. Apple\r\n2. Banana\n3. Cherry\r\n"u8.ToArray();
        LineCursor cursor = new(block, DefaultMaxLineLength);

        Assert.True(cursor.TryReadLine(out int offsetA, out int lengthA));
        Assert.Equal("1. Apple", Encoding.UTF8.GetString(block, offsetA, lengthA));
        Assert.True(cursor.TryReadLine(out int offsetB, out int lengthB));
        Assert.Equal("2. Banana", Encoding.UTF8.GetString(block, offsetB, lengthB));
        Assert.True(cursor.TryReadLine(out int offsetC, out int lengthC));
        Assert.Equal("3. Cherry", Encoding.UTF8.GetString(block, offsetC, lengthC));
        Assert.False(cursor.TryReadLine(out _, out _));
    }

    [Fact]
    [Trait("Case", "CB-10")]
    public void Reading_a_block_beginning_with_a_byte_order_mark_excludes_it_from_the_first_line()
    {
        byte[] block = [0xEF, 0xBB, 0xBF, .. "1. Apple\n"u8.ToArray()];
        LineCursor cursor = new(block, DefaultMaxLineLength);

        Assert.True(cursor.TryReadLine(out int offset, out int length));
        Assert.Equal(3, offset);
        Assert.Equal("1. Apple", Encoding.UTF8.GetString(block, offset, length));
    }

    [Fact]
    [Trait("Case", "CB-11")]
    public void Reading_a_block_where_the_mark_is_the_only_content_before_a_terminator_does_not_throw()
    {
        byte[] block = [0xEF, 0xBB, 0xBF, (byte)'\n'];
        LineCursor cursor = new(block, DefaultMaxLineLength);

        bool found = cursor.TryReadLine(out int offset, out int length);

        // The cursor's only notion of "malformed" is the length-limit exception; the
        // mark contributes no bytes to whatever is reported, throw or not.
        Assert.True(found);
        Assert.Equal(3, offset);
        Assert.Equal(0, length);
        Assert.False(cursor.TryReadLine(out _, out _));
    }

    [Fact]
    [Trait("Case", "CB-12")]
    public void Reading_a_line_one_byte_beyond_the_limit_is_malformed_at_the_limit()
    {
        const int maxLineLength = 20;
        byte[][] blocks = [new byte[8], new byte[8], new byte[5]]; // 21 bytes, no terminator anywhere
        Array.Fill(blocks[0], (byte)'A');
        Array.Fill(blocks[1], (byte)'B');
        Array.Fill(blocks[2], (byte)'C');

        MalformedLineException exception = Assert.Throws<MalformedLineException>(
            () => ReadAcrossBlocks(blocks, maxLineLength, out _));

        Assert.Equal(0, exception.ByteOffset);
        Assert.Equal(1, exception.LineNumber);
        Assert.NotEmpty(exception.Preview);
    }

    [Fact]
    [Trait("Case", "CB-13")]
    public void Reading_empty_input_emits_no_lines()
    {
        LineCursor cursor = new([], DefaultMaxLineLength);

        Assert.False(cursor.TryReadLine(out _, out _));
        Assert.Equal(0, cursor.CarryLength);
    }

    [Fact]
    [Trait("Case", "CB-14")]
    public void Reading_a_line_ending_with_two_carriage_returns_strips_only_one()
    {
        byte[] block = "1. Apple\r\r\n"u8.ToArray();
        LineCursor cursor = new(block, DefaultMaxLineLength);

        Assert.True(cursor.TryReadLine(out int offset, out int length));
        Assert.Equal("1. Apple\r", Encoding.UTF8.GetString(block, offset, length));
    }

    [Fact]
    [Trait("Case", "CB-15")]
    public void Reading_a_lone_carriage_return_mid_line_treats_it_as_ordinary_content()
    {
        byte[] block = "1. App\rle\n"u8.ToArray();
        LineCursor cursor = new(block, DefaultMaxLineLength);

        Assert.True(cursor.TryReadLine(out int offset, out int length));
        Assert.Equal("1. App\rle", Encoding.UTF8.GetString(block, offset, length));
    }

    [Fact]
    [Trait("Case", "CB-16")]
    public void Reading_a_line_one_byte_inside_the_limit_spanning_several_reads_is_accepted()
    {
        const int maxLineLength = 40;
        string content = new('Y', 36); // one byte short of the limit once "9. " is added
        byte[] whole = Encoding.UTF8.GetBytes($"9. {content}\n");

        byte[][] blocks = [whole[..10], whole[10..20], whole[20..]];
        List<string> lines = ReadAcrossBlocks(blocks, maxLineLength, out int finalCarryLength);

        Assert.Equal([$"9. {content}"], lines);
        Assert.Equal(0, finalCarryLength);
    }

    [Fact]
    [Trait("Case", "CB-17")]
    public void Reading_a_block_ending_exactly_on_the_carriage_return_of_a_maximum_length_crlf_line_carries_it_rather_than_throwing()
    {
        // The no-newline branch must bound the content alone, not the raw carry: the
        // trailing CR is a terminator byte whose other half has not arrived yet, so it
        // must not count against maxLineLength. Bounding the raw carry instead rejects
        // a legal maxLineLength-byte \r\n line whenever a fill boundary lands on its CR.
        const int maxLineLength = 8;
        byte[] block = "1. abcde\r"u8.ToArray(); // content "1. abcde" is exactly maxLineLength bytes
        LineCursor cursor = new(block, maxLineLength);

        bool found = cursor.TryReadLine(out _, out _);

        Assert.False(found); // carried, not thrown
        Assert.Equal(0, cursor.CarryOffset);
        Assert.Equal(9, cursor.CarryLength); // the full 9 bytes, CR included
    }

    [Fact]
    [Trait("Case", "CB-18")]
    public void Reading_a_crlf_line_whose_content_already_exceeds_the_limit_still_throws_once_terminated()
    {
        // The other side of the case above: excluding a trailing, still-unterminated CR
        // from the no-newline branch's bound must not loosen the terminated branch,
        // which excludes the CR from contentEnd before comparing against maxLineLength.
        // A genuinely over-length \r\n line -- content one byte past the limit -- still
        // throws once its terminator has arrived.
        const int maxLineLength = 8;
        byte[] block = "1. abcdef\r\n"u8.ToArray(); // content "1. abcdef" is 9 bytes, one past the limit

        MalformedLineException exception = Assert.Throws<MalformedLineException>(() =>
        {
            LineCursor cursor = new(block, maxLineLength);
            cursor.TryReadLine(out _, out _);
        });

        Assert.Equal(0, exception.ByteOffset);
    }

    [Fact]
    [Trait("Case", "CB-19")]
    public void Reading_with_carriage_return_stripping_disabled_keeps_the_carriage_return_as_content()
    {
        // RunCursor and OutputVerifier's output-side scan both pass
        // stripCarriageReturn: false, because a run file -- and this sorter's own
        // output -- is terminated by a bare '\n' throughout, so a '\r' immediately
        // before one is always content and never a terminator's second half.
        byte[] block = "1. Apple\r\n2. Banana\r\n"u8.ToArray();
        LineCursor cursor = new(block, DefaultMaxLineLength, stripCarriageReturn: false);

        Assert.True(cursor.TryReadLine(out int offsetA, out int lengthA));
        Assert.Equal("1. Apple\r", Encoding.UTF8.GetString(block, offsetA, lengthA));
        Assert.True(cursor.TryReadLine(out int offsetB, out int lengthB));
        Assert.Equal("2. Banana\r", Encoding.UTF8.GetString(block, offsetB, lengthB));
        Assert.False(cursor.TryReadLine(out _, out _));
    }

    // Mimics ChunkReader: copy the trailing carry to the front of the next read and
    // hand LineCursor the two concatenated. Concatenating rather than copying in place
    // is enough here, since this exercises LineCursor's boundary logic and not the
    // buffer-reuse discipline that belongs to ChunkReader.
    private static List<string> ReadAcrossBlocks(
        IEnumerable<byte[]> reads, int maxLineLength, out int finalCarryLength, Action<int>? observeCarry = null)
    {
        List<string> lines = [];
        byte[] pending = [];

        foreach (byte[] block in reads)
        {
            byte[] combined = [.. pending, .. block];
            LineCursor cursor = new(combined, maxLineLength);

            while (cursor.TryReadLine(out int offset, out int length))
            {
                lines.Add(Encoding.UTF8.GetString(combined, offset, length));
            }

            pending = combined[cursor.CarryOffset..(cursor.CarryOffset + cursor.CarryLength)];
            observeCarry?.Invoke(pending.Length);
        }

        finalCarryLength = pending.Length;
        return lines;
    }
}
