using System.Text;
using FileSorter.LineFormat;
using FileSorter.RunGeneration;
using FileSorter.Tests.LineFormat;
using Xunit;

namespace FileSorter.Tests.RunGeneration;

public sealed class ChunkSorterTests
{
    [Fact]
    [Trait("Case", "CS-01")]
    public void Sorting_an_already_sorted_chunk_leaves_it_in_order()
    {
        byte[] buffer = "1. Apple\n2. Banana\n3. Cherry\n"u8.ToArray();
        LineDescriptor[] descriptors = Build(buffer);

        ChunkSorter.Sort(descriptors, buffer);

        Assert.Equal(["1. Apple", "2. Banana", "3. Cherry"], ReadBack(descriptors, buffer));
    }

    [Fact]
    [Trait("Case", "CS-02")]
    public void Sorting_a_reverse_ordered_chunk_produces_ascending_order()
    {
        byte[] buffer = "3. Cherry\n2. Banana\n1. Apple\n"u8.ToArray();
        LineDescriptor[] descriptors = Build(buffer);

        ChunkSorter.Sort(descriptors, buffer);

        Assert.Equal(["1. Apple", "2. Banana", "3. Cherry"], ReadBack(descriptors, buffer));
    }

    [Fact]
    [Trait("Case", "CS-03")]
    public void Sorting_a_chunk_of_identical_lines_changes_nothing_observable()
    {
        byte[] buffer = "9. Same\n9. Same\n9. Same\n"u8.ToArray();
        LineDescriptor[] descriptors = Build(buffer);

        ChunkSorter.Sort(descriptors, buffer);

        Assert.Equal(["9. Same", "9. Same", "9. Same"], ReadBack(descriptors, buffer));
    }

    [Fact]
    [Trait("Case", "CS-04")]
    public void Sorting_a_chunk_with_one_shared_string_part_orders_by_number_then_raw_bytes()
    {
        byte[] buffer = "5. Apple\n-3. Apple\n007. Apple\n7. Apple\n0. Apple\n"u8.ToArray();
        LineDescriptor[] descriptors = Build(buffer);

        ChunkSorter.Sort(descriptors, buffer);

        Assert.Equal(["-3. Apple", "0. Apple", "5. Apple", "007. Apple", "7. Apple"], ReadBack(descriptors, buffer));
    }

    [Fact]
    [Trait("Case", "CS-05")]
    public void Sorting_a_single_line_chunk_is_a_no_op()
    {
        byte[] buffer = "42. Only\n"u8.ToArray();
        LineDescriptor[] descriptors = Build(buffer);

        ChunkSorter.Sort(descriptors, buffer);

        Assert.Equal(["42. Only"], ReadBack(descriptors, buffer));
    }

    [Fact]
    [Trait("Case", "CS-06")]
    public void Sorting_an_empty_chunk_does_not_throw()
    {
        LineDescriptor[] descriptors = [];

        ChunkSorter.Sort(descriptors, []);

        Assert.Empty(descriptors);
    }

    [Fact]
    [Trait("Case", "CS-07")]
    public void Sorting_preserves_the_multiset_of_lines()
    {
        byte[] buffer = "3. Cherry\n1. Apple\n1. Apple\n2. Banana\n3. Cherry\n"u8.ToArray();
        LineDescriptor[] descriptors = Build(buffer);
        string[] before = ReadBack(descriptors, buffer);

        ChunkSorter.Sort(descriptors, buffer);

        string[] after = ReadBack(descriptors, buffer);
        Assert.Equal(before.OrderBy(x => x, StringComparer.Ordinal), after.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(before.Length, after.Length);
    }

    [Fact]
    [Trait("Case", "CS-08")]
    public void Sorting_produces_the_same_order_across_repeated_runs()
    {
        byte[] buffer = "9. filler\n007. Apple\n7. Apple\n1. Zebra\n"u8.ToArray();

        LineDescriptor[] runOne = Build(buffer);
        ChunkSorter.Sort(runOne, buffer);
        string[] orderOne = ReadBack(runOne, buffer);

        LineDescriptor[] runTwo = Build(buffer);
        ChunkSorter.Sort(runTwo, buffer);
        string[] orderTwo = ReadBack(runTwo, buffer);

        Assert.Equal(orderOne, orderTwo);
    }

    [Fact]
    [Trait("Case", "CS-08")]
    public void Sorting_the_same_leading_zero_tie_agrees_regardless_of_which_chunk_it_lands_in()
    {
        // The same pair in two differently composed chunks: different surrounding
        // lines, different offsets. Only the raw bytes decide this order, so chunk
        // boundaries must not change it however the input was cut.
        byte[] chunkA = "9. filler\n007. Apple\n7. Apple\n"u8.ToArray();
        byte[] chunkB = "1. Zebra\n2. Banana\n007. Apple\n7. Apple\n3. Cherry\n"u8.ToArray();

        LineDescriptor[] descriptorsA = Build(chunkA);
        LineDescriptor[] descriptorsB = Build(chunkB);
        ChunkSorter.Sort(descriptorsA, chunkA);
        ChunkSorter.Sort(descriptorsB, chunkB);

        Assert.Equal("007. Apple", WinnerOfTie(descriptorsA, chunkA));
        Assert.Equal("007. Apple", WinnerOfTie(descriptorsB, chunkB));
    }

    [Fact]
    [Trait("Case", "CS-09")]
    public void Sorting_a_chunk_where_every_string_part_shares_a_top_byte_still_orders_correctly()
    {
        // "Apple", "Apricot", "Avocado", "Az" all bucket together under 'A' -- the
        // whole chunk lands in one bucket, so this exercises the introsort finishing
        // step on its own, with the bucketing pass reduced to a no-op.
        byte[] buffer = "1. Az\n2. Apple\n3. Avocado\n4. Apricot\n"u8.ToArray();
        LineDescriptor[] descriptors = Build(buffer);

        ChunkSorter.Sort(descriptors, buffer);

        Assert.Equal(["2. Apple", "4. Apricot", "3. Avocado", "1. Az"], ReadBack(descriptors, buffer));
    }

    [Fact]
    [Trait("Case", "CS-10")]
    public void Sorting_a_chunk_with_empty_string_parts_puts_them_first()
    {
        // An empty string part has Prefix zero (BuildPrefix zero-pads), so it must
        // land in bucket 0 -- ahead of every non-empty string part -- exactly where
        // ordinal order already puts an empty string against any non-empty one.
        byte[] buffer = "3. Zebra\n2. \n5. Apple\n1. \n"u8.ToArray();
        LineDescriptor[] descriptors = Build(buffer);

        ChunkSorter.Sort(descriptors, buffer);

        Assert.Equal(["1. ", "2. ", "5. Apple", "3. Zebra"], ReadBack(descriptors, buffer));
    }

    [Fact]
    [Trait("Case", "CS-11")]
    public void Sorting_strings_sharing_a_top_byte_but_differing_later_orders_by_the_later_bytes()
    {
        // Same bucket ('A'), and the eight-byte prefix also differs between these
        // three, so this is the ordinary within-bucket comparison rather than the
        // full-prefix tie covered below.
        byte[] buffer = "1. Ab\n2. Aa\n3. Ac\n"u8.ToArray();
        LineDescriptor[] descriptors = Build(buffer);

        ChunkSorter.Sort(descriptors, buffer);

        Assert.Equal(["2. Aa", "1. Ab", "3. Ac"], ReadBack(descriptors, buffer));
    }

    [Fact]
    [Trait("Case", "CS-12")]
    public void Sorting_strings_whose_full_eight_byte_prefix_ties_still_orders_by_the_bytes_after_it()
    {
        // Both string parts start with the same eight bytes ("AAAAAAAA"), so Prefix
        // itself ties and the bucketing pass alone cannot distinguish them -- this
        // reaches LineOrder's full string comparison, still inside a single bucket.
        byte[] buffer = "1. AAAAAAAAY\n2. AAAAAAAAX\n"u8.ToArray();
        LineDescriptor[] descriptors = Build(buffer);

        ChunkSorter.Sort(descriptors, buffer);

        Assert.Equal(["2. AAAAAAAAX", "1. AAAAAAAAY"], ReadBack(descriptors, buffer));
    }

    [Fact]
    [Trait("Case", "CS-13")]
    public void Sorting_string_parts_shorter_than_eight_bytes_orders_by_the_zero_padded_prefix()
    {
        // "A" is a proper prefix of "AB", so ordinal order (and the zero-padded
        // Prefix that stands in for it) puts the shorter one first; "Z" starts a
        // different, later bucket entirely.
        byte[] buffer = "1. Z\n2. AB\n3. A\n"u8.ToArray();
        LineDescriptor[] descriptors = Build(buffer);

        ChunkSorter.Sort(descriptors, buffer);

        Assert.Equal(["3. A", "2. AB", "1. Z"], ReadBack(descriptors, buffer));
    }

    [Fact]
    [Trait("Case", "CS-14")]
    public void Sorting_string_parts_that_agree_for_eight_bytes_orders_by_the_ninth_and_tenth()
    {
        // The seam: byte 9 of the string part is the first byte the cached prefix does
        // not cover, so these three reach the radix's string-byte levels with nothing
        // left for Prefix to decide, and are separated at depth 8 and at depth 9.
        const string Filler = "4. " + Head + "zz";

        string[] sorted = SortLatin1(["1. " + Head + "AB", "2. " + Head + "AA", "3. " + Head + "B"], Filler);

        Assert.Equal(
            Expected(Filler, "2. " + Head + "AA", "1. " + Head + "AB", "3. " + Head + "B"),
            sorted);
    }

    [Fact]
    [Trait("Case", "CS-15")]
    public void Sorting_a_string_part_of_exactly_eight_bytes_puts_it_before_its_own_extension()
    {
        // The case the padded prefix cannot decide on its own: both have the same
        // Prefix, and only the first string-byte level separates them -- the shorter
        // one into bucket 0, which orders ahead of every real byte.
        const string Filler = "3. " + Head + "zz";

        string[] sorted = SortLatin1(["1. " + Head + "A", "2. " + Head], Filler);

        Assert.Equal(Expected(Filler, "2. " + Head, "1. " + Head + "A"), sorted);
    }

    [Fact]
    [Trait("Case", "CS-16")]
    public void Sorting_a_string_part_whose_ninth_byte_is_a_real_zero_still_puts_the_shorter_one_first()
    {
        // What forces bucket 0 to be a bucket of its own rather than the byte value
        // zero: "the string part ended" orders strictly before a real 0x00, which in
        // turn orders before every other byte.
        const string Filler = "4. " + Head + "zz";

        string[] sorted = SortLatin1(["1. " + Head + "A", "2. " + Head + "\0", "3. " + Head], Filler);

        Assert.Equal(
            Expected(Filler, "3. " + Head, "2. " + Head + "\0", "1. " + Head + "A"),
            sorted);
    }

    [Fact]
    [Trait("Case", "CS-17")]
    public void Sorting_string_parts_that_differ_only_in_trailing_zero_bytes_orders_the_shortest_first()
    {
        // All three pad to the same eight-byte prefix, so all three land in bucket 0 at
        // the first string-byte level -- which is exactly why that bucket is finished
        // with the full comparator instead of being recursed into: what it holds there
        // is not necessarily equal, only equal as far as the padded prefix could see.
        const string Filler = "4. ab\0\0\0\0\0\0z";

        string[] sorted = SortLatin1(["1. ab\0\0", "2. ab", "3. ab\0"], Filler);

        Assert.Equal(Expected(Filler, "2. ab", "3. ab\0", "1. ab\0\0"), sorted);
    }

    [Fact]
    [Trait("Case", "CS-18")]
    public void Sorting_string_parts_that_agree_past_the_depth_cap_still_orders_by_the_bytes_after_it()
    {
        // Twenty shared bytes is past the radix's depth cap, so this range reaches the
        // cap and is finished by the introsort under the full comparator. That path is
        // what keeps the cap a bound on work and not on the order.
        const string Twenty = "ABCDEFGHIJKLMNOPQRST";
        const string Filler = "4. " + Twenty + "z";

        string[] sorted = SortLatin1(["1. " + Twenty + "B", "2. " + Twenty + "A", "3. " + Twenty + "C"], Filler);

        Assert.Equal(
            Expected(Filler, "2. " + Twenty + "A", "1. " + Twenty + "B", "3. " + Twenty + "C"),
            sorted);
    }

    [Fact]
    [Trait("Case", "CS-19")]
    public void Sorting_string_parts_that_share_twelve_bytes_orders_by_the_thirteenth()
    {
        // Twelve shared bytes means the counting pass at depths 0 through 11 finds
        // everything in one bucket: the split guard skips each of those permutations
        // and retries a byte deeper in the same stack frame, and depth 12 is where the
        // range actually splits.
        const string Twelve = "SHAREDPREFIX";
        const string Filler = "4. " + Twelve + "z";

        string[] sorted = SortLatin1(["1. " + Twelve + "B", "2. " + Twelve + "A", "3. " + Twelve + "C"], Filler);

        Assert.Equal(
            Expected(Filler, "2. " + Twelve + "A", "1. " + Twelve + "B", "3. " + Twelve + "C"),
            sorted);
    }

    [Fact]
    [Trait("Case", "CS-20")]
    public void Sorting_empty_string_parts_among_long_ones_still_puts_them_first()
    {
        // Empty string parts again, this time in a chunk large enough to reach the
        // radix rather than its introsort base case, with the non-empty string parts
        // long enough to descend several string-byte levels: an empty string part is
        // bucket 0 at every depth, which is where ordinal order already puts it.
        const string Long = "LONGSTRINGPART";
        const string Filler = "4. " + Long + "z";

        string[] sorted = SortLatin1(["1. ", "2. " + Long + "A", "3. "], Filler);

        Assert.Equal(Expected(Filler, "1. ", "3. ", "2. " + Long + "A"), sorted);
    }

    // The eight bytes the cached prefix covers exactly, shared by the seam cases so
    // they reach the radix's string-byte levels with nothing left to decide on Prefix.
    private const string Head = "SEAMHEAD";

    // ChunkSorter finishes any range of 32 or fewer descriptors with the introsort
    // alone, so a curated case of three or four lines would never reach the radix at
    // all. Each case above states the lines it is about and then repeats one filler
    // line -- sharing the same leading bytes, so it stays in the same range rather than
    // splitting off at the first level -- until the chunk is past that threshold. The
    // filler always sorts last, so it never comes between the lines under test.
    private const int FillerLines = 40;

    /// Sorts <paramref name="lines"/> followed by <see cref="FillerLines"/> copies of
    /// <paramref name="fillerLine"/> and reads the result back. Latin-1 throughout, so
    /// a string part can carry any byte value -- 0x00 included -- and still be written
    /// and asserted as an ordinary string literal.
    private static string[] SortLatin1(string[] lines, string fillerLine)
    {
        string[] all = [.. lines, .. Enumerable.Repeat(fillerLine, FillerLines)];
        byte[] buffer = Encoding.Latin1.GetBytes(string.Join('\n', all) + "\n");
        LineDescriptor[] descriptors = Build(buffer);

        ChunkSorter.Sort(descriptors, buffer);

        return [.. descriptors.Select(d => Encoding.Latin1.GetString(buffer, d.Offset, d.Length))];
    }

    private static string[] Expected(string fillerLine, params string[] orderedLines) =>
        [.. orderedLines, .. Enumerable.Repeat(fillerLine, FillerLines)];

    private static string WinnerOfTie(LineDescriptor[] sorted, byte[] buffer)
    {
        string[] lines = ReadBack(sorted, buffer);
        return Array.Find(lines, line => line is "007. Apple" or "7. Apple")!;
    }

    private static LineDescriptor[] Build(byte[] buffer) => [.. DescriptorFixture.Build(buffer)];

    private static string[] ReadBack(LineDescriptor[] descriptors, byte[] buffer) =>
        [.. descriptors.Select(d => Encoding.UTF8.GetString(buffer, d.Offset, d.Length))];
}
